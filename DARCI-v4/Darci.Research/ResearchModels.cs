namespace Darci.Research;

/// <summary>
/// Represents a research session — a bounded investigation with a defined goal.
/// Created by DARCI's Research action or by an external research agent.
/// </summary>
public record ResearchSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>active | completed | failed | cancelled</summary>
    public string Status { get; init; } = "active";

    /// <summary>Who started this session: DARCI, an agent name, or a user ID.</summary>
    public string CreatedBy { get; init; } = "DARCI";

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; init; }

    /// <summary>JSON-serialised string[] of topic tags.</summary>
    public string Tags { get; init; } = "[]";
}

/// <summary>
/// A single result item within a research session.
/// </summary>
public record ResearchResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string SessionId { get; init; } = "";

    /// <summary>Where the result came from: web, memory, tool, agent, darci.</summary>
    public string Source { get; init; } = "";

    public string Content { get; init; } = "";

    /// <summary>text | json | code | url | summary</summary>
    public string ResultType { get; init; } = "text";

    /// <summary>JSON-serialised string[] of tags.</summary>
    public string Tags { get; init; } = "[]";

    /// <summary>0.0–1.0 relevance to the session goal.</summary>
    public float RelevanceScore { get; init; } = 0f;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Tracks one sub-agent job within a research session.
/// </summary>
public record ResearchAgentJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = "";
    public string SubQuestion { get; init; } = "";
    public string AgentType { get; init; } = "web";
    public string Status { get; init; } = "queued";
    public DateTime? AssignedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ResultSummary { get; init; }
    public float? Confidence { get; init; }
    public string? Error { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A file artifact produced during a research session (reports, exports, generated code, etc.).
/// The file itself lives on disk at FilePath; this record is the index entry.
/// </summary>
public record ResearchFile
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string SessionId { get; init; } = "";
    public string Filename { get; init; } = "";

    /// <summary>MIME type, e.g. application/json, text/markdown, application/pdf.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>Absolute path on the host where the file is stored.</summary>
    public string FilePath { get; init; } = "";

    public long SizeBytes { get; init; } = 0;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

// ─── Request / response DTOs (used by Darci.Api) ────────────────────────────

public record CreateSessionRequest(string Title, string Description, string? CreatedBy, string[]? Tags);
public record AddResultRequest(string Source, string Content, string? ResultType, string[]? Tags, float? RelevanceScore);
public record SessionSummary(ResearchSession Session, int ResultCount, int FileCount);
