#nullable enable

using System.Diagnostics;
using System.Text.RegularExpressions;
using Darci.Research;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents.Agents;

public sealed class WebResearchAgent : IResearchAgent
{
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IResearchStore _store;
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<WebResearchAgent> _logger;

    public WebResearchAgent(IResearchStore store, IResearchToolbox toolbox, ILogger<WebResearchAgent> logger)
    {
        _store = store;
        _toolbox = toolbox;
        _logger = logger;
    }

    public string AgentType => "web";

    public async Task<AgentReport> RunAsync(string jobId, string sessionId, string subQuestion, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _store.UpdateAgentJobAsync(jobId, "running", assignedAt: startedAt);

            var raw = await _toolbox.SearchWebAsync(subQuestion, ct);
            var prompt = $"""
Summarize the following research notes into 2-5 factual sentences.
If the notes are weak or uncertain, say that plainly.
Notes:
{raw}
""";
            var summary = await _toolbox.GenerateAsync(prompt, ct);
            var sourceRef = UrlRegex.Match(raw).Success ? UrlRegex.Match(raw).Value : null;

            await _store.AddResultAsync(sessionId, "web", summary, "summary", relevanceScore: 0.45f);
            await _store.UpdateAgentJobAsync(
                jobId,
                "done",
                resultSummary: summary,
                confidence: 0.45f,
                assignedAt: startedAt,
                completedAt: DateTime.UtcNow);

            return new AgentReport
            {
                JobId = jobId,
                AgentType = AgentType,
                SubQuestion = subQuestion,
                IsSuccess = true,
                Summary = summary,
                Confidence = 0.45f,
                SourceRef = sourceRef,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebResearchAgent failed for job {JobId}", jobId);
            await _store.UpdateAgentJobAsync(
                jobId,
                "failed",
                error: ex.Message,
                assignedAt: startedAt,
                completedAt: DateTime.UtcNow);

            return new AgentReport
            {
                JobId = jobId,
                AgentType = AgentType,
                SubQuestion = subQuestion,
                IsSuccess = false,
                Error = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }
}
