#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Creates and restores git checkpoints around agent edits.
/// All operations are confined to the workspace root via SafeCommandRunner.
///
/// <para><b>A checkpoint may never claim work it did not make.</b> This used to run <c>git add -A</c> and
/// commit, which takes the whole repository — so when the autonomous loop picked up a task while someone
/// was editing, their in-progress files were committed under a "[DARCI checkpoint]" message describing an
/// unrelated task. That happened. Two things prevent it now, and they are deliberately redundant:</para>
///
/// <list type="number">
///   <item><description><b>Mode.</b> A checkpoint taken BEFORE any edits anchors HEAD and commits nothing,
///     because at that moment every dirty file in the tree belongs to someone else. Committing is opt-in,
///     and the default is the safe one.</description></item>
///   <item><description><b>Scope.</b> When a checkpoint does commit, it stages explicit paths inside the
///     workspace rather than <c>-A</c>. A workspace that is a subdirectory can no longer reach its
///     siblings even by accident.</description></item>
/// </list>
///
/// <para>Rollback is the mirror image of the same hazard — <c>git reset --hard</c> discards every tracked
/// modification in the repository, not just the agent's — so it stashes first. Untracked files survive a
/// hard reset untouched, so a plain stash plus the reset leaves nothing unrecoverable.</para>
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
        string workspaceId,
        string taskId,
        string message,
        CheckpointMode mode = CheckpointMode.AnchorHeadOnly,
        CancellationToken ct = default)
    {
        var workspace = await _store.GetWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return null;

        var commitMsg = $"[DARCI checkpoint] task={taskId}: {TruncateMessage(message)}";
        var dirty = await DirtyPathsAsync(workspaceId, ct);

        if (mode == CheckpointMode.AnchorHeadOnly)
        {
            // Nothing is staged and nothing is committed. Anything dirty right now pre-dates the agent,
            // so capturing it would be claiming authorship of someone else's work.
            if (dirty.Count > 0)
            {
                _logger.LogWarning(
                    "Workspace {Id} has {Count} uncommitted change(s) that do NOT belong to task {TaskId} "
                    + "({Sample}). The checkpoint anchors HEAD and commits nothing; those files are left alone.",
                    workspaceId, dirty.Count, taskId, string.Join(", ", dirty.Take(5)));
            }

            return await AnchorAsync(workspaceId, taskId, commitMsg, ct);
        }

        // ── CommitWorkspaceChanges ──
        var inScope = await InScopePathsAsync(workspaceId, dirty, ct);
        if (inScope.Count == 0)
        {
            _logger.LogDebug("No in-scope changes to checkpoint for workspace {Id}.", workspaceId);
            return await AnchorAsync(workspaceId, taskId, commitMsg, ct);
        }

        // Explicit pathspec, never `-A`: the argument list IS the scope, so a path outside the workspace
        // cannot be staged even if git would otherwise consider it.
        //
        // `:(top)` matters. Pathspecs on the command line are relative to the working directory, while
        // `git status --porcelain` reports relative to the repository root — mixing the two silently
        // staged nothing for a workspace in a subdirectory, which then looked like a successful
        // checkpoint that had quietly captured no work at all.
        var addArgs = new List<string> { "add", "--" };
        addArgs.AddRange(inScope.Select(p => $":(top){p}"));
        var addResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest("git", addArgs.ToArray()), ct);

        if (!addResult.WasAllowed)
        {
            _logger.LogWarning("git add was not allowed in workspace {Id}. Checkpoint skipped.", workspaceId);
            return null;
        }

        var commitResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "commit", "-m", commitMsg }), ct);

        if (commitResult.ExitCode != 0 && !SaysNothingToCommit(commitResult))
        {
            _logger.LogWarning("git commit failed for workspace {Id}: {Stderr}", workspaceId, commitResult.StderrTail);
            return null;
        }

        _logger.LogInformation(
            "Checkpoint for workspace {Id} committed {Count} in-scope path(s).", workspaceId, inScope.Count);
        return await AnchorAsync(workspaceId, taskId, commitMsg, ct);
    }

    /// <summary>Records a checkpoint pointing at the CURRENT HEAD, whatever that is.</summary>
    private async Task<CodingCheckpoint?> AnchorAsync(string workspaceId, string taskId, string message, CancellationToken ct)
    {
        var sha = await HeadShaAsync(workspaceId, ct);
        if (string.IsNullOrWhiteSpace(sha))
        {
            _logger.LogWarning("Could not determine HEAD SHA for workspace {Id}; no checkpoint recorded.", workspaceId);
            return null;
        }

        var checkpoint = new CodingCheckpoint
        {
            WorkspaceId = workspaceId,
            TaskId = taskId,
            CommitSha = sha,
            Message = message,
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

        // A hard reset discards every tracked modification in the repository — the agent's edits, which is
        // the point, and anyone else's, which is not. Stash first so the destructive half is recoverable.
        // Untracked files are not touched by a hard reset, so this covers everything at risk.
        var dirty = await DirtyPathsAsync(workspaceId, ct);
        var stashed = false;
        if (dirty.Count > 0)
        {
            var stash = await _runner.RunAsync(workspaceId, new CodingCommandRequest("git", new[] { "stash" }), ct);
            stashed = stash.ExitCode == 0;
            if (!stashed)
            {
                _logger.LogError(
                    "Refusing to roll back workspace {Id}: {Count} uncommitted change(s) could not be stashed "
                    + "({Stderr}), and a hard reset would destroy them.",
                    workspaceId, dirty.Count, stash.StderrTail);
                return false;
            }

            _logger.LogWarning(
                "Stashed {Count} uncommitted change(s) in workspace {Id} before rolling back. "
                + "Recover them with `git stash pop`.", dirty.Count, workspaceId);
        }

        var resetResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "reset", "--hard", checkpoint.CommitSha }), ct);

        if (resetResult.ExitCode != 0)
        {
            _logger.LogError("git reset --hard {Sha} failed for workspace {Id}: {Stderr}",
                checkpoint.CommitSha, workspaceId, resetResult.StderrTail);
            return false;
        }

        _logger.LogInformation("Rolled back workspace {WorkspaceId} to checkpoint {Sha}{Stash}.",
            workspaceId, checkpoint.CommitSha, stashed ? " (previous changes are in the stash)" : "");
        return true;
    }

    // ── git helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Repository-relative paths with uncommitted changes, staged or not, tracked or not.</summary>
    private async Task<IReadOnlyList<string>> DirtyPathsAsync(string workspaceId, CancellationToken ct)
    {
        var status = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "status", "--porcelain" }), ct);

        if (!status.WasAllowed || status.ExitCode != 0) return Array.Empty<string>();

        var paths = new List<string>();
        foreach (var raw in status.StdoutTail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4) continue;

            var path = line[3..].Trim();

            // A rename reads "R  old -> new"; the new name is the one that exists.
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];

            path = path.Trim('"');
            if (path.Length > 0) paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    /// The subset of <paramref name="dirty"/> that lies inside this workspace. Paths from
    /// <c>git status</c> are repository-relative, so they are compared against the workspace's own
    /// position in the repository.
    /// </summary>
    private async Task<IReadOnlyList<string>> InScopePathsAsync(
        string workspaceId, IReadOnlyList<string> dirty, CancellationToken ct)
    {
        var prefixResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "rev-parse", "--show-prefix" }), ct);

        var prefix = prefixResult.ExitCode == 0
            ? prefixResult.StdoutTail.Replace("\r", "").Trim().Trim('/')
            : "";

        // An empty prefix means the workspace IS the repository root, so every path is nominally in
        // scope. That is exactly the case where scoping buys nothing and the mode check is what protects
        // the tree — which is why both exist.
        if (prefix.Length == 0) return dirty;

        var needle = prefix + "/";
        return dirty.Where(p => p.StartsWith(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<string> HeadShaAsync(string workspaceId, CancellationToken ct)
    {
        var revResult = await _runner.RunAsync(workspaceId, new CodingCommandRequest(
            "git", new[] { "rev-parse", "HEAD" }), ct);

        return revResult.StdoutTail.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
    }

    private static bool SaysNothingToCommit(CodingCommandRun run) =>
        run.StdoutTail.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
        || run.StderrTail.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase);

    private static string TruncateMessage(string message, int maxLength = 60)
    {
        message = message.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return message.Length > maxLength ? message[..maxLength] + "…" : message;
    }
}
