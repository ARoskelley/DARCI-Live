#nullable enable

using Darci.Research.Agents;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Detects roadblocks from repeated command failures and triggers deep research.
/// Triggers when 3+ consecutive non-zero exits or repeated identical stderr patterns.
/// </summary>
public sealed class RoadblockDetector : IRoadblockDetector
{
    // 2 consecutive failures (not 3) so research can still be used on the final retry.
    // With MaxRetries = 3: after attempt 1 fails we have 2 runs → threshold met → research
    // triggered → attempt 2 has the research in context.
    private const int ConsecutiveFailureThreshold = 2;

    private readonly ICodingWorkspaceStore _store;
    private readonly IDeepResearchOrchestrator _research;
    private readonly ILogger<RoadblockDetector> _logger;

    public RoadblockDetector(
        ICodingWorkspaceStore store,
        IDeepResearchOrchestrator research,
        ILogger<RoadblockDetector> logger)
    {
        _store = store;
        _research = research;
        _logger = logger;
    }

    public async Task<string?> CheckAndResearchAsync(
        string workspaceId,
        string taskId,
        string failingCommand,
        string stderrSnippet,
        CancellationToken ct = default)
    {
        var recentRuns = await _store.GetRecentCommandRunsForTaskAsync(taskId, ConsecutiveFailureThreshold + 2, ct);

        // Check if last N runs are all failures.
        var lastRuns = recentRuns.Take(ConsecutiveFailureThreshold).ToList();
        var allFailed = lastRuns.Count >= ConsecutiveFailureThreshold
            && lastRuns.All(r => r.ExitCode != 0 || r.TimedOut);

        // Check for repeated identical stderr.
        var repeatedError = false;
        if (lastRuns.Count >= 2)
        {
            var stderrNorm = NormalizeStderr(stderrSnippet);
            var matchCount = lastRuns
                .Where(r => !string.IsNullOrWhiteSpace(r.StderrTail))
                .Count(r => NormalizeStderr(r.StderrTail).Contains(stderrNorm[..Math.Min(80, stderrNorm.Length)],
                    StringComparison.OrdinalIgnoreCase));
            repeatedError = matchCount >= 2;
        }

        if (!allFailed && !repeatedError)
        {
            return null;
        }

        _logger.LogInformation(
            "Roadblock detected for task {TaskId}: consecutiveFails={Consecutive} repeatedError={Repeated}. Triggering deep research.",
            taskId, allFailed, repeatedError);

        var workspace = await _store.GetWorkspaceAsync(workspaceId, ct);
        var workspaceName = workspace?.Name ?? workspaceId;
        var question = BuildResearchQuestion(workspaceName, failingCommand, stderrSnippet);

        try
        {
            var outcome = await _research.RunDeepResearchAsync(question, "DARCI", ct);
            if (outcome.IsSuccess && !string.IsNullOrWhiteSpace(outcome.FinalAnswer))
            {
                _logger.LogInformation("Deep research returned synthesis for task {TaskId}.", taskId);
                return TruncateSynthesis(outcome.FinalAnswer, 2000);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deep research failed for roadblock on task {TaskId}.", taskId);
        }

        return $"[Roadblock detected: {ConsecutiveFailureThreshold}+ consecutive failures. Research inconclusive.]\nError: {stderrSnippet[..Math.Min(400, stderrSnippet.Length)]}";
    }

    public async Task<string?> ResearchTopicAsync(string question, CancellationToken ct = default)
    {
        _logger.LogInformation("Proactive research triggered: {Question}", question.Length > 120 ? question[..120] : question);
        try
        {
            var outcome = await _research.RunDeepResearchAsync(question, "DARCI", ct);
            if (outcome.IsSuccess && !string.IsNullOrWhiteSpace(outcome.FinalAnswer))
            {
                _logger.LogInformation("Proactive research returned synthesis ({Len} chars).", outcome.FinalAnswer.Length);
                return TruncateSynthesis(outcome.FinalAnswer, 2000);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Proactive research failed (non-fatal).");
        }

        return null;
    }

    private static string BuildResearchQuestion(string workspaceName, string command, string stderr)
    {
        var errorSnippet = stderr.Length > 300 ? stderr[..300] : stderr;
        errorSnippet = errorSnippet.Trim();
        return $"[Workspace: {workspaceName}] The command '{command}' is failing with: {errorSnippet}. " +
               "What are the likely causes and how can this be fixed?";
    }

    private static string NormalizeStderr(string stderr) =>
        System.Text.RegularExpressions.Regex.Replace(stderr.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string TruncateSynthesis(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "\n[truncated]";
}
