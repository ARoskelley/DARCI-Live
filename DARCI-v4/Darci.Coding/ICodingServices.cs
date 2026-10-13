#nullable enable

namespace Darci.Coding;

/// <summary>Persists coding workspaces, files, command runs, tasks, embeddings, and checkpoints.</summary>
public interface ICodingWorkspaceStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task UpsertWorkspaceAsync(CodingWorkspace workspace, IReadOnlyList<CodingFileEntry> files, CancellationToken ct = default);
    Task<IReadOnlyList<CodingWorkspace>> GetWorkspacesAsync(int limit = 50, CancellationToken ct = default);
    Task<CodingWorkspace?> GetWorkspaceAsync(string workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<CodingFileEntry>> GetFilesAsync(string workspaceId, int limit = 500, CancellationToken ct = default);
    Task AddCommandRunAsync(CodingCommandRun run, CancellationToken ct = default);
    Task<IReadOnlyList<CodingCommandRun>> GetCommandRunsAsync(string workspaceId, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<CodingCommandRun>> GetRecentCommandRunsForTaskAsync(string taskId, int limit = 10, CancellationToken ct = default);
    Task AddTaskAsync(CodingTaskRecord task, CancellationToken ct = default);
    Task UpdateTaskAsync(CodingTaskRecord task, CancellationToken ct = default);
    Task<CodingTaskRecord?> GetTaskAsync(string taskId, CancellationToken ct = default);
    Task<IReadOnlyList<CodingTaskRecord>> GetTasksAsync(string? workspaceId = null, int limit = 50, CancellationToken ct = default);

    // Embeddings
    Task UpsertFileEmbeddingAsync(string fileId, string workspaceId, float[] embedding, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, float[]>> GetFileEmbeddingsAsync(string workspaceId, CancellationToken ct = default);

    // Checkpoints
    Task AddCheckpointAsync(CodingCheckpoint checkpoint, CancellationToken ct = default);
    Task<CodingCheckpoint?> GetLatestCheckpointAsync(string workspaceId, string taskId, CancellationToken ct = default);
}

/// <summary>Scans a local directory and imports it as a coding workspace.</summary>
public interface IWorkspaceScanner
{
    Task<CodingWorkspaceImportResult> ImportAsync(CodingWorkspaceImportRequest request, CancellationToken ct = default);
}

/// <summary>Builds a context package of relevant files and KG hits for a given query.</summary>
public interface ICodingContextBuilder
{
    Task<CodingContextPackage> BuildAsync(string workspaceId, string? query = null, int limit = 8, CancellationToken ct = default);
}

/// <summary>Runs allowlisted shell commands safely inside a workspace root.</summary>
public interface ISafeCommandRunner
{
    Task<CodingCommandRun> RunAsync(string workspaceId, CodingCommandRequest request, CancellationToken ct = default);
    Task<CodingCommandRun> RunForTaskAsync(string workspaceId, string taskId, CodingCommandRequest request, CancellationToken ct = default);
}

/// <summary>Creates coding tasks with LLM-generated or template plans.</summary>
public interface ICodingTaskService
{
    Task<CodingTaskRecord> CreateTaskAsync(CreateCodingTaskRequest request, CancellationToken ct = default);
}

/// <summary>
/// What a checkpoint is allowed to do to the repository.
///
/// <para>The distinction exists because the two callers mean genuinely different things, and conflating
/// them was a bug: the agent loop's PRE-RUN checkpoint committed the entire working tree with
/// <c>git add -A</c> before the agent had edited anything, so everything it captured was by definition
/// somebody else's in-progress work — committed under a "[DARCI checkpoint]" message describing a task it
/// had nothing to do with.</para>
/// </summary>
public enum CheckpointMode
{
    /// <summary>
    /// Record where HEAD is and commit NOTHING. The correct meaning of a checkpoint taken before any
    /// edits: it is a rollback target, not a snapshot of work in progress.
    /// </summary>
    AnchorHeadOnly = 0,

    /// <summary>
    /// Commit the workspace's own changes — for a checkpoint taken AFTER edits, where committing is the
    /// point. Still scoped to the workspace path; never the whole repository.
    /// </summary>
    CommitWorkspaceChanges = 1,
}

/// <summary>Creates and restores git checkpoints around edits.</summary>
public interface IGitCheckpointService
{
    /// <summary>Creates a checkpoint. Returns null if git is not initialised or fails.</summary>
    Task<CodingCheckpoint?> CreateCheckpointAsync(
        string workspaceId,
        string taskId,
        string message,
        CheckpointMode mode = CheckpointMode.AnchorHeadOnly,
        CancellationToken ct = default);

    /// <summary>Restores the workspace to the given checkpoint commit.</summary>
    Task<bool> RollbackToCheckpointAsync(string workspaceId, string taskId, string checkpointId, CancellationToken ct = default);
}

/// <summary>Computes and caches file embeddings for a workspace in the background.</summary>
public interface IWorkspaceEmbeddingService
{
    Task EnrichAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>Extracts code symbols from workspace files and creates KG nodes.</summary>
public interface IKgEnrichmentService
{
    Task EnrichAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>Drives the autonomous edit–test–debug loop for a coding task.</summary>
public interface ICodingAgentLoop
{
    /// <summary>
    /// Starts the loop for the given task in the background. Returns false if already running.
    /// When <paramref name="rootPacket"/> is supplied (the loop was reached via the node router), the
    /// run's audit log continues on that packet; otherwise a fresh packet is minted (direct REST path).
    /// </summary>
    bool StartLoop(string taskId, RunCodingTaskRequest? options = null, Darci.Nodes.NodePacket? rootPacket = null);
    /// <summary>Returns true if a loop is currently running for the given task.</summary>
    bool IsRunning(string taskId);
    Task<CodingTaskStatusResponse?> GetStatusAsync(string taskId, CancellationToken ct = default);
}

/// <summary>Detects roadblocks from repeated command failures and triggers deep research.</summary>
public interface IRoadblockDetector
{
    /// <summary>
    /// Checks whether a roadblock condition has been reached (repeated failures) and, if so,
    /// runs deep research. Returns the research summary, or null if no research was triggered.
    /// </summary>
    Task<string?> CheckAndResearchAsync(string workspaceId, string taskId, string failingCommand, string stderrSnippet, CancellationToken ct = default);

    /// <summary>
    /// Directly triggers deep research on an arbitrary question, e.g. for proactive
    /// low-confidence escalation before a failure actually occurs.
    /// Returns the research summary, or null if research failed or is unavailable.
    /// </summary>
    Task<string?> ResearchTopicAsync(string question, CancellationToken ct = default);
}
