#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Creates and restores git checkpoints around agent edits.
/// All operations are confined to the workspace root via SafeCommandRunner.
/// </summary>
public sealed class GitCheckpointService : IGitCheckpointService
{
    private readonly ICodingWorkspaceStore _store;
    private readonly ISafeCommandRunner _runner;
    private readonly ILogger<GitCheckpointService> _logger;

    public GitCheckpointService(
        ICodingWorkspaceStore store,
        ISafeCommandRunner runner,
        ILogger<GitCheckpointService> logger)
    {
        _store = store;
        _runner = runner;
        _logger = logger;
    }

    public async Task<CodingCheckpoint?> CreateCheckpointAsync(
        string workspaceId, string taskId, string message, CancellationToken ct = default)
    {
        var workspace = await _store.GetWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return null;

        // Stage all tracked and untracked changes.
        var addResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "add", "-A" }), ct);

        if (!addResult.WasAllowed)
        {
            _logger.LogWarning("git add -A was not allowed in workspace {Id}. Checkpoint skipped.", workspaceId);
            return null;
        }

        // Create the commit.
        var commitMsg = $"[DARCI checkpoint] task={taskId}: {TruncateMessage(message)}";
        var commitResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "commit", "-m", commitMsg }), ct);

        if (commitResult.ExitCode != 0)
        {
            // Nothing to commit is fine (exit code 1 with "nothing to commit").
            if (commitResult.StdoutTail.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                || commitResult.StderrTail.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("No changes to checkpoint for workspace {Id}.", workspaceId);
                // Return a checkpoint with the current HEAD SHA so rollback still works.
            }
            else
            {
                _logger.LogWarning("git commit failed for workspace {Id}: {Stderr}", workspaceId, commitResult.StderrTail);
                return null;
            }
        }

        // Get the commit SHA.
        var revResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "rev-parse", "HEAD" }), ct);

        var sha = revResult.StdoutTail.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(sha))
        {
            _logger.LogWarning("Could not determine HEAD SHA after checkpoint for workspace {Id}.", workspaceId);
            return null;
        }

        var checkpoint = new CodingCheckpoint
        {
            WorkspaceId = workspaceId,
            TaskId = taskId,
            CommitSha = sha,
            Message = commitMsg
        };

        await _store.AddCheckpointAsync(checkpoint, ct);
        _logger.LogInformation("Created checkpoint {Id} at {Sha} for task {TaskId}.", checkpoint.Id, sha, taskId);
        return checkpoint;
    }

    public async Task<bool> RollbackToCheckpointAsync(
        string workspaceId, string taskId, string checkpointId, CancellationToken ct = default)
    {
        var checkpoint = await _store.GetLatestCheckpointAsync(workspaceId, taskId, ct);
        if (checkpoint is null)
        {
            _logger.LogWarning("No checkpoint found for workspace {WorkspaceId} task {TaskId}.", workspaceId, taskId);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(checkpointId) && checkpoint.Id != checkpointId)
        {
            _logger.LogWarning("Checkpoint ID mismatch: requested {Requested}, latest is {Latest}.", checkpointId, checkpoint.Id);
        }

        // Hard reset to the checkpoint commit.
        var resetResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "reset", "--hard", checkpoint.CommitSha }), ct);

        if (resetResult.ExitCode != 0)
        {
            _logger.LogError("git reset --hard {Sha} failed for workspace {Id}: {Stderr}",
                checkpoint.CommitSha, workspaceId, resetResult.StderrTail);
            return false;
        }

        _logger.LogInformation("Rolled back workspace {WorkspaceId} to checkpoint {Sha}.",
            workspaceId, checkpoint.CommitSha);
        return true;
    }

    private static string TruncateMessage(string message, int maxLength = 60)
    {
        message = message.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return message.Length > maxLength ? message[..maxLength] + "…" : message;
    }
}
