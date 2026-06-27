#nullable enable

using Darci.Nodes;

namespace Darci.Coding;

public sealed record CodingWorkspaceImportRequest(
    string RootPath,
    string? Name = null,
    string? CreatedBy = null,
    string[]? Tags = null);

public sealed record CodingCommandRequest(
    string Command,
    string[]? Arguments = null,
    string? WorkingSubdirectory = null,
    int? TimeoutSeconds = null);

public sealed record CreateCodingTaskRequest(
    string WorkspaceId,
    string Prompt,
    string? SuccessCriteria = null,
    string? CreatedBy = null);

public sealed record CodingContextRequest(
    string? Query = null,
    int? Limit = null);

public sealed record CodingWorkspace
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string RootPath { get; init; } = "";
    public string CreatedBy { get; init; } = "DARCI";
    public string Status { get; init; } = "active";
    public string Summary { get; init; } = "";
    public string[] Tags { get; init; } = Array.Empty<string>();
    public string[] DetectedLanguages { get; init; } = Array.Empty<string>();
    public string[] DetectedCommands { get; init; } = Array.Empty<string>();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastIndexedAt { get; init; }
}

public sealed record CodingFileEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Extension { get; init; } = "";
    public string Kind { get; init; } = "file";
    public long SizeBytes { get; init; }
    public bool IsText { get; init; }
    public DateTime LastModifiedUtc { get; init; }
}

public sealed record CodingWorkspaceImportResult(
    CodingWorkspace Workspace,
    IReadOnlyList<CodingFileEntry> Files,
    int SkippedFileCount,
    string[] Warnings);

public sealed record CodingContextPackage
{
    public string WorkspaceId { get; init; } = "";
    public string Query { get; init; } = "";
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public string[] SuggestedCommands { get; init; } = Array.Empty<string>();
    public string[] Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CodingContextFile> Files { get; init; } = Array.Empty<CodingContextFile>();
    public IReadOnlyList<CodingKgHit> KgHits { get; init; } = Array.Empty<CodingKgHit>();
}

public sealed record CodingContextFile
{
    public string RelativePath { get; init; } = "";
    public string Kind { get; init; } = "";
    public long SizeBytes { get; init; }
    public float RelevanceScore { get; init; }
    public string Preview { get; init; } = "";
}

public sealed record CodingCommandRun
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Command { get; init; } = "";
    public string[] Arguments { get; init; } = Array.Empty<string>();
    public string DisplayCommand { get; init; } = "";
    public int? ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool WasAllowed { get; init; }
    public string StdoutTail { get; init; } = "";
    public string StderrTail { get; init; } = "";
    public string? Error { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}

public sealed record CodingTaskRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string SuccessCriteria { get; init; } = "";
    public string CreatedBy { get; init; } = "DARCI";
    public string Status { get; init; } = "planning";
    public string Plan { get; init; } = "";
    public string LastContextPackageJson { get; init; } = "";
    public string PlanGeneratedBy { get; init; } = "template";
    public string PlanModel { get; init; } = "";
    public int CurrentStepIndex { get; init; }
    public string LastStepResult { get; init; } = "";
    public string RoadblockResearch { get; init; } = "";
    /// <summary>Output of the last behavioral verification run (dotnet test or equivalent).</summary>
    public string VerificationResult { get; init; } = "";
    /// <summary>
    /// Self-assessed confidence from the last LLM response (unified <see cref="Darci.Nodes.Confidence"/>;
    /// <see cref="Darci.Nodes.Confidence.Unassessed"/> = not yet evaluated). Gap/low detection via IsGap/IsLow.
    /// </summary>
    public Confidence Confidence { get; init; } = Confidence.Unassessed;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>A single structured step within a coding task plan.</summary>
public sealed record CodingPlanStep
{
    public int StepNumber { get; init; }
    public string Description { get; init; } = "";
    public string Status { get; init; } = "pending"; // pending | in_progress | completed | failed | roadblocked
    public string? Result { get; init; }
    /// <summary>
    /// Self-assessed confidence from the last LLM response for this step (unified type;
    /// <see cref="Darci.Nodes.Confidence.Unassessed"/> = not yet evaluated).
    /// </summary>
    public Confidence Confidence { get; init; } = Confidence.Unassessed;
}

/// <summary>A git checkpoint created before edits are applied.</summary>
public sealed record CodingCheckpoint
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string WorkspaceId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string CommitSha { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>A knowledge-graph entity hit included in a context package.</summary>
public sealed record CodingKgHit
{
    public string EntityId { get; init; } = "";
    public string Name { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string Domain { get; init; } = "";
    public string? Description { get; init; }
    public float RelevanceScore { get; init; }
}

/// <summary>Status response for a running or completed coding task.</summary>
public sealed record CodingTaskStatusResponse
{
    public string TaskId { get; init; } = "";
    public string Status { get; init; } = "";
    public int CurrentStepIndex { get; init; }
    public int TotalSteps { get; init; }
    public string? CurrentStepDescription { get; init; }
    public string LastStepResult { get; init; } = "";
    public string RoadblockResearch { get; init; } = "";
    public string VerificationResult { get; init; } = "";
    /// <summary>Unified confidence projection for the last LLM response (replaces flat score/note).</summary>
    public Confidence Confidence { get; init; } = Confidence.Unassessed;
    public bool IsRunning { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record RunCodingTaskRequest(
    string? SuccessVerificationCommand = null,
    string[]? SuccessVerificationArguments = null);
