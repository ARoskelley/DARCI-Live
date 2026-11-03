#nullable enable

using System.Diagnostics;
using Darci.Coding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Coding.Tests;

/// <summary>
/// A checkpoint may never commit work it did not make.
///
/// <para>These run against a REAL git repository in a temp directory, because the bug they pin was a bug
/// about what git actually did: the pre-run checkpoint ran <c>git add -A</c> and committed, which takes
/// the whole repository. When the autonomous loop picked up a task while a developer was mid-edit, their
/// files were committed under a "[DARCI checkpoint]" message describing an unrelated task. A mocked
/// command runner would have asserted the arguments and proved nothing about the outcome.</para>
/// </summary>
public sealed class GitCheckpointScopeTests : IDisposable
{
    private readonly string _repo;
    private readonly string _dbPath;
    private readonly CodingWorkspaceStore _store;
    private readonly GitCheckpointService _checkpoints;
    private string _workspaceId = "";

    public GitCheckpointScopeTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"darci-ckpt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);

        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");   // so checkout does not rewrite line endings

        Directory.CreateDirectory(Path.Combine(_repo, "theirs"));
        File.WriteAllText(Path.Combine(_repo, "theirs", "in-progress.cs"), "// committed state\n");
        Directory.CreateDirectory(Path.Combine(_repo, "agent-workspace"));
        File.WriteAllText(Path.Combine(_repo, "agent-workspace", "code.cs"), "// committed state\n");
        Git("add", "-A");
        Git("commit", "-qm", "baseline");

        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-ckpt-{Guid.NewGuid():N}.db");
        _store = new CodingWorkspaceStore($"Data Source={_dbPath}", NullLogger<CodingWorkspaceStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();

        var runner = new SafeCommandRunner(_store, NullLogger<SafeCommandRunner>.Instance);
        _checkpoints = new GitCheckpointService(_store, runner, NullLogger<GitCheckpointService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        try { DeleteTree(_repo); } catch { /* best effort */ }
    }

    // ── the bug ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APreRunCheckpoint_DoesNotCommitSomebodyElsesInProgressWork()
    {
        // Exactly the situation that happened: a developer is editing, and the autonomous loop starts a
        // task. Before the fix this produced a "[DARCI checkpoint]" commit containing their files.
        await UseWorkspaceAtRepoRootAsync();
        File.WriteAllText(Path.Combine(_repo, "theirs", "in-progress.cs"), "// THEIR UNCOMMITTED WORK\n");
        File.WriteAllText(Path.Combine(_repo, "theirs", "brand-new.cs"), "// their new file\n");
        var headBefore = Head();

        var checkpoint = await _checkpoints.CreateCheckpointAsync(
            _workspaceId, "task-1", "Pre-run checkpoint for task: something unrelated",
            CheckpointMode.AnchorHeadOnly);

        Assert.NotNull(checkpoint);
        Assert.Equal(headBefore, Head());                       // no commit was created at all
        Assert.Equal(headBefore, checkpoint!.CommitSha);        // and the anchor points at the real HEAD

        // Their work is exactly where they left it: still uncommitted, still on disk, still theirs.
        Assert.Contains("theirs/in-progress.cs", Status());
        Assert.Contains("theirs/brand-new.cs", Status());
        Assert.Equal("// THEIR UNCOMMITTED WORK\n", File.ReadAllText(Path.Combine(_repo, "theirs", "in-progress.cs")));
    }

    [Fact]
    public async Task APreRunCheckpoint_StagesNothing_SoALaterCommitCannotPickItUpEither()
    {
        // Leaving files staged would be nearly as bad: the next commit from any source sweeps them in.
        await UseWorkspaceAtRepoRootAsync();
        File.WriteAllText(Path.Combine(_repo, "theirs", "in-progress.cs"), "// theirs\n");

        await _checkpoints.CreateCheckpointAsync(_workspaceId, "task-1", "pre-run", CheckpointMode.AnchorHeadOnly);

        Assert.Empty(RunGit("diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public async Task TheAnchorIsAUsableRollbackTarget_EvenThoughItCommittedNothing()
    {
        // The checkpoint still has to do its job: name a commit the agent's work can be undone back to.
        await UseWorkspaceAtRepoRootAsync();

        var checkpoint = await _checkpoints.CreateCheckpointAsync(
            _workspaceId, "task-1", "pre-run", CheckpointMode.AnchorHeadOnly);

        Assert.NotNull(checkpoint);
        Assert.Equal(40, checkpoint!.CommitSha.Length);
        Assert.Equal(Head(), checkpoint.CommitSha);
    }

    // ── scoping, the second line of defence ──────────────────────────────────────────────────────

    [Fact]
    public async Task ACommittingCheckpoint_TakesOnlyPathsInsideItsOwnWorkspace()
    {
        // A workspace that is a subdirectory must not be able to reach its siblings, even when the
        // caller explicitly asked to commit.
        await UseWorkspaceAsync(Path.Combine(_repo, "agent-workspace"));
        File.WriteAllText(Path.Combine(_repo, "agent-workspace", "code.cs"), "// the agent's edit\n");
        File.WriteAllText(Path.Combine(_repo, "theirs", "in-progress.cs"), "// THEIR UNCOMMITTED WORK\n");

        var headBefore = Head();

        await _checkpoints.CreateCheckpointAsync(
            _workspaceId, "task-1", "post-edit", CheckpointMode.CommitWorkspaceChanges);

        // A commit actually happened — without this, staging nothing at all would pass the two
        // assertions below by leaving HEAD on the baseline.
        Assert.NotEqual(headBefore, Head());

        // The agent's file went in...
        var committed = RunGit("show", "--name-only", "--format=", "HEAD");
        Assert.Contains("agent-workspace/code.cs", committed);
        Assert.DoesNotContain("theirs/in-progress.cs", committed);

        // ...and theirs is still sitting there uncommitted.
        Assert.Contains("theirs/in-progress.cs", Status());
    }

    [Fact]
    public async Task ACommittingCheckpointWithNothingOfItsOwn_StillAnchorsRatherThanCommitting()
    {
        await UseWorkspaceAsync(Path.Combine(_repo, "agent-workspace"));
        File.WriteAllText(Path.Combine(_repo, "theirs", "in-progress.cs"), "// theirs only\n");
        var headBefore = Head();

        var checkpoint = await _checkpoints.CreateCheckpointAsync(
            _workspaceId, "task-1", "post-edit", CheckpointMode.CommitWorkspaceChanges);

        Assert.Equal(headBefore, Head());
        Assert.Equal(headBefore, checkpoint!.CommitSha);
        Assert.Contains("theirs/in-progress.cs", Status());
    }

    [Fact]
    public async Task TheDefaultModeIsTheSafeOne()
    {
        // A new caller that forgets to think about this gets the mode that cannot destroy anything.
        await UseWorkspaceAtRepoRootAsync();
        File.WriteAllText(Path.Combine(_repo, "theirs", "in-progress.cs"), "// theirs\n");
        var headBefore = Head();

        await _checkpoints.CreateCheckpointAsync(_workspaceId, "task-1", "no mode given");

        Assert.Equal(headBefore, Head());
        Assert.Contains("theirs/in-progress.cs", Status());
    }

    [Fact]
    public async Task TheModeIsWhatMakesTheDifference_AndTheOldBehaviourIsStillReachableOnRequest()
    {
        // Characterises the fix rather than just asserting it: the SAME situation, at the repository
        // root, with the committing mode, does sweep the whole tree — which is what the pre-run
        // checkpoint used to do unconditionally. So the mode is load-bearing, not decorative.
        //
        // This path is only reachable when an operator explicitly asks for a checkpoint via
        // /coding/workspaces/{id}/checkpoint, where "commit what is here" is the request. At the
        // repository root there is no narrower scope to apply, and that residual is recorded here
        // deliberately rather than left to be discovered.
        await UseWorkspaceAtRepoRootAsync();
        File.WriteAllText(Path.Combine(_repo, "theirs", "in-progress.cs"), "// theirs" + Environment.NewLine);
        var headBefore = Head();

        await _checkpoints.CreateCheckpointAsync(
            _workspaceId, "task-1", "operator asked", CheckpointMode.CommitWorkspaceChanges);

        Assert.NotEqual(headBefore, Head());
        Assert.Contains("theirs/in-progress.cs", RunGit("show", "--name-only", "--format=", "HEAD"));
    }

    // ── rollback, the mirror image ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackStashesUncommittedWork_InsteadOfDestroyingIt()
    {
        // `git reset --hard` discards every tracked modification in the repository, the agent's and
        // everyone else's alike. After the pre-run fix this is where a developer's work was still at
        // risk, so the destructive half has to be recoverable.
        await UseWorkspaceAtRepoRootAsync();
        var checkpoint = await _checkpoints.CreateCheckpointAsync(
            _workspaceId, "task-1", "pre-run", CheckpointMode.AnchorHeadOnly);

        File.WriteAllText(Path.Combine(_repo, "theirs", "in-progress.cs"), "// THEIR UNCOMMITTED WORK\n");

        var ok = await _checkpoints.RollbackToCheckpointAsync(_workspaceId, "task-1", checkpoint!.Id);

        Assert.True(ok);
        Assert.Equal(checkpoint.CommitSha, Head());
        // The working copy is back at the checkpoint, and their edit is retrievable rather than gone.
        Assert.Equal("// committed state\n", File.ReadAllText(Path.Combine(_repo, "theirs", "in-progress.cs")));
        Assert.Contains("stash", RunGit("stash", "list"), StringComparison.OrdinalIgnoreCase);

        RunGit("stash", "pop");
        Assert.Equal("// THEIR UNCOMMITTED WORK\n", File.ReadAllText(Path.Combine(_repo, "theirs", "in-progress.cs")));
    }

    [Fact]
    public async Task RollbackWithACleanTree_DoesNotCreateAPointlessStash()
    {
        await UseWorkspaceAtRepoRootAsync();
        var checkpoint = await _checkpoints.CreateCheckpointAsync(
            _workspaceId, "task-1", "pre-run", CheckpointMode.AnchorHeadOnly);

        var ok = await _checkpoints.RollbackToCheckpointAsync(_workspaceId, "task-1", checkpoint!.Id);

        Assert.True(ok);
        Assert.Empty(RunGit("stash", "list").Trim());
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private Task UseWorkspaceAtRepoRootAsync() => UseWorkspaceAsync(_repo);

    private async Task UseWorkspaceAsync(string rootPath)
    {
        var workspace = new CodingWorkspace { Name = "test", RootPath = rootPath, CreatedBy = "test" };
        await _store.UpsertWorkspaceAsync(workspace, Array.Empty<CodingFileEntry>());
        _workspaceId = workspace.Id;
    }

    private string Head() => RunGit("rev-parse", "HEAD").Trim();

    private string Status() => RunGit("status", "--porcelain").Replace('\\', '/');

    private void Git(params string[] args) => RunGit(args);

    private string RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        return stdout;
    }

    private static void DeleteTree(string path)
    {
        // git marks objects read-only, which Directory.Delete refuses.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
