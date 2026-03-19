using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research;

/// <summary>
/// SQLite-backed implementation of <see cref="IResearchStore"/>.
/// All tables live in the same darci.db file used by the rest of DARCI.
/// </summary>
public sealed class ResearchStore : IResearchStore
{
    private readonly string _connectionString;
    private readonly ILogger<ResearchStore> _logger;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public ResearchStore(string connectionString, ILogger<ResearchStore>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger ?? NullLogger<ResearchStore>.Instance;
    }

    public async Task InitializeAsync()
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS research_sessions (
                id           TEXT PRIMARY KEY,
                title        TEXT NOT NULL,
                description  TEXT NOT NULL DEFAULT '',
                status       TEXT NOT NULL DEFAULT 'active',
                created_by   TEXT NOT NULL DEFAULT 'DARCI',
                tags         TEXT NOT NULL DEFAULT '[]',
                created_at   TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS research_results (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL REFERENCES research_sessions(id),
                source          TEXT NOT NULL,
                content         TEXT NOT NULL,
                result_type     TEXT NOT NULL DEFAULT 'text',
                tags            TEXT NOT NULL DEFAULT '[]',
                relevance_score REAL NOT NULL DEFAULT 0.0,
                created_at      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS research_files (
                id           TEXT PRIMARY KEY,
                session_id   TEXT NOT NULL REFERENCES research_sessions(id),
                filename     TEXT NOT NULL,
                content_type TEXT NOT NULL DEFAULT 'application/octet-stream',
                file_path    TEXT NOT NULL,
                size_bytes   INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS research_agent_jobs (
                id              TEXT PRIMARY KEY,
                session_id      TEXT NOT NULL REFERENCES research_sessions(id),
                sub_question    TEXT NOT NULL,
                agent_type      TEXT NOT NULL DEFAULT 'web',
                status          TEXT NOT NULL DEFAULT 'queued',
                assigned_at     TEXT,
                completed_at    TEXT,
                result_summary  TEXT,
                confidence      REAL,
                error           TEXT,
                created_at      TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_results_session ON research_results(session_id);
            CREATE INDEX IF NOT EXISTS ix_files_session   ON research_files(session_id);
            CREATE INDEX IF NOT EXISTS ix_sessions_status ON research_sessions(status);
            CREATE INDEX IF NOT EXISTS ix_agent_jobs_session ON research_agent_jobs(session_id);
            CREATE INDEX IF NOT EXISTS ix_agent_jobs_status  ON research_agent_jobs(status);
            """);

        _logger.LogInformation("ResearchStore tables ready.");
    }

    public async Task<ResearchSession> CreateSessionAsync(
        string title,
        string description,
        string createdBy = "DARCI",
        string[]? tags = null)
    {
        var session = new ResearchSession
        {
            Title = title,
            Description = description,
            CreatedBy = createdBy,
            Tags = JsonSerializer.Serialize(tags ?? Array.Empty<string>(), Json)
        };

        using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO research_sessions (id, title, description, status, created_by, tags, created_at)
            VALUES (@Id, @Title, @Description, @Status, @CreatedBy, @Tags, @CreatedAt)
            """,
            new
            {
                session.Id,
                session.Title,
                session.Description,
                session.Status,
                session.CreatedBy,
                session.Tags,
                CreatedAt = session.CreatedAt.ToString("O")
            });

        return session;
    }

    public async Task<ResearchSession?> GetSessionAsync(string sessionId)
    {
        using var conn = Open();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM research_sessions WHERE id = @Id",
            new { Id = sessionId });

        return row is null ? null : MapSession(row);
    }

    public async Task<IReadOnlyList<ResearchSession>> GetSessionsAsync(string? status = null, int limit = 50)
    {
        using var conn = Open();
        var sql = status is null
            ? "SELECT * FROM research_sessions ORDER BY created_at DESC LIMIT @Limit"
            : "SELECT * FROM research_sessions WHERE status = @Status ORDER BY created_at DESC LIMIT @Limit";

        var rows = await conn.QueryAsync<dynamic>(sql, new { Status = status, Limit = limit });
        return rows.Select(MapSession).ToList();
    }

    public async Task<bool> CompleteSessionAsync(string sessionId, string status = "completed")
    {
        using var conn = Open();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE research_sessions
            SET status = @Status, completed_at = @Now
            WHERE id = @Id
            """,
            new { Status = status, Now = DateTime.UtcNow.ToString("O"), Id = sessionId });

        return affected > 0;
    }

    public async Task<ResearchResult> AddResultAsync(
        string sessionId,
        string source,
        string content,
        string resultType = "text",
        string[]? tags = null,
        float relevanceScore = 0f)
    {
        var result = new ResearchResult
        {
            SessionId = sessionId,
            Source = source,
            Content = content,
            ResultType = resultType,
            Tags = JsonSerializer.Serialize(tags ?? Array.Empty<string>(), Json),
            RelevanceScore = relevanceScore
        };

        using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO research_results
                (id, session_id, source, content, result_type, tags, relevance_score, created_at)
            VALUES
                (@Id, @SessionId, @Source, @Content, @ResultType, @Tags, @RelevanceScore, @CreatedAt)
            """,
            new
            {
                result.Id,
                result.SessionId,
                result.Source,
                result.Content,
                result.ResultType,
                result.Tags,
                result.RelevanceScore,
                CreatedAt = result.CreatedAt.ToString("O")
            });

        return result;
    }

    public async Task<IReadOnlyList<ResearchResult>> GetResultsAsync(string sessionId, string? resultType = null)
    {
        using var conn = Open();
        var sql = resultType is null
            ? "SELECT * FROM research_results WHERE session_id = @SessionId ORDER BY created_at"
            : "SELECT * FROM research_results WHERE session_id = @SessionId AND result_type = @ResultType ORDER BY created_at";

        var rows = await conn.QueryAsync<dynamic>(sql, new { SessionId = sessionId, ResultType = resultType });
        return rows.Select(MapResult).ToList();
    }

    public async Task<IReadOnlyList<ResearchResult>> SearchResultsAsync(string query, int limit = 20)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT * FROM research_results
            WHERE content LIKE @Pattern OR source LIKE @Pattern
            ORDER BY relevance_score DESC, created_at DESC
            LIMIT @Limit
            """,
            new { Pattern = $"%{query}%", Limit = limit });

        return rows.Select(MapResult).ToList();
    }

    public async Task<ResearchAgentJob> CreateAgentJobAsync(
        string sessionId,
        string subQuestion,
        string agentType,
        string status = "queued")
    {
        var job = new ResearchAgentJob
        {
            SessionId = sessionId,
            SubQuestion = subQuestion,
            AgentType = agentType,
            Status = status
        };

        using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO research_agent_jobs
                (id, session_id, sub_question, agent_type, status, assigned_at, completed_at, result_summary, confidence, error, created_at)
            VALUES
                (@Id, @SessionId, @SubQuestion, @AgentType, @Status, @AssignedAt, @CompletedAt, @ResultSummary, @Confidence, @Error, @CreatedAt)
            """,
            new
            {
                job.Id,
                job.SessionId,
                job.SubQuestion,
                job.AgentType,
                job.Status,
                AssignedAt = job.AssignedAt?.ToString("O"),
                CompletedAt = job.CompletedAt?.ToString("O"),
                job.ResultSummary,
                job.Confidence,
                job.Error,
                CreatedAt = job.CreatedAt.ToString("O")
            });

        return job;
    }

    public async Task<ResearchAgentJob?> GetAgentJobAsync(string jobId)
    {
        using var conn = Open();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM research_agent_jobs WHERE id = @Id",
            new { Id = jobId });

        return row is null ? null : MapAgentJob(row);
    }

    public async Task<IReadOnlyList<ResearchAgentJob>> GetAgentJobsAsync(string sessionId)
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT * FROM research_agent_jobs
            WHERE session_id = @SessionId
            ORDER BY created_at
            """,
            new { SessionId = sessionId });

        return rows.Select(MapAgentJob).ToList();
    }

    public async Task<bool> UpdateAgentJobAsync(
        string jobId,
        string status,
        string? resultSummary = null,
        float? confidence = null,
        string? error = null,
        DateTime? assignedAt = null,
        DateTime? completedAt = null)
    {
        using var conn = Open();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE research_agent_jobs
            SET status = @Status,
                assigned_at = @AssignedAt,
                completed_at = @CompletedAt,
                result_summary = @ResultSummary,
                confidence = @Confidence,
                error = @Error
            WHERE id = @Id
            """,
            new
            {
                Id = jobId,
                Status = status,
                AssignedAt = assignedAt?.ToString("O"),
                CompletedAt = completedAt?.ToString("O"),
                ResultSummary = resultSummary,
                Confidence = confidence,
                Error = error
            });

        return affected > 0;
    }

    public async Task<ResearchFile> RegisterFileAsync(
        string sessionId,
        string filename,
        string contentType,
        string filePath,
        long sizeBytes)
    {
        var file = new ResearchFile
        {
            SessionId = sessionId,
            Filename = filename,
            ContentType = contentType,
            FilePath = filePath,
            SizeBytes = sizeBytes
        };

        using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO research_files (id, session_id, filename, content_type, file_path, size_bytes, created_at)
            VALUES (@Id, @SessionId, @Filename, @ContentType, @FilePath, @SizeBytes, @CreatedAt)
            """,
            new
            {
                file.Id,
                file.SessionId,
                file.Filename,
                file.ContentType,
                file.FilePath,
                file.SizeBytes,
                CreatedAt = file.CreatedAt.ToString("O")
            });

        return file;
    }

    public async Task<IReadOnlyList<ResearchFile>> GetFilesAsync(string? sessionId = null)
    {
        using var conn = Open();
        var sql = sessionId is null
            ? "SELECT * FROM research_files ORDER BY created_at DESC"
            : "SELECT * FROM research_files WHERE session_id = @SessionId ORDER BY created_at DESC";

        var rows = await conn.QueryAsync<dynamic>(sql, new { SessionId = sessionId });
        return rows.Select(MapFile).ToList();
    }

    public async Task<ResearchFile?> GetFileAsync(string fileId)
    {
        using var conn = Open();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM research_files WHERE id = @Id",
            new { Id = fileId });

        return row is null ? null : MapFile(row);
    }

    public async Task<bool> DeleteFileAsync(string fileId)
    {
        using var conn = Open();
        var affected = await conn.ExecuteAsync(
            "DELETE FROM research_files WHERE id = @Id",
            new { Id = fileId });

        return affected > 0;
    }

    public async Task<SessionSummary?> GetSessionSummaryAsync(string sessionId)
    {
        var session = await GetSessionAsync(sessionId);
        if (session is null)
        {
            return null;
        }

        using var conn = Open();
        var resultCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM research_results WHERE session_id = @Id",
            new { Id = sessionId });
        var fileCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM research_files WHERE session_id = @Id",
            new { Id = sessionId });

        return new SessionSummary(session, resultCount, fileCount);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static ResearchSession MapSession(dynamic row) => new()
    {
        Id = (string)row.id,
        Title = (string)row.title,
        Description = (string)row.description,
        Status = (string)row.status,
        CreatedBy = (string)row.created_by,
        Tags = (string)row.tags,
        CreatedAt = DateTime.Parse((string)row.created_at),
        CompletedAt = row.completed_at is null ? null : DateTime.Parse((string)row.completed_at)
    };

    private static ResearchResult MapResult(dynamic row) => new()
    {
        Id = (string)row.id,
        SessionId = (string)row.session_id,
        Source = (string)row.source,
        Content = (string)row.content,
        ResultType = (string)row.result_type,
        Tags = (string)row.tags,
        RelevanceScore = (float)(double)row.relevance_score,
        CreatedAt = DateTime.Parse((string)row.created_at)
    };

    private static ResearchAgentJob MapAgentJob(dynamic row) => new()
    {
        Id = (string)row.id,
        SessionId = (string)row.session_id,
        SubQuestion = (string)row.sub_question,
        AgentType = (string)row.agent_type,
        Status = (string)row.status,
        AssignedAt = row.assigned_at is null ? null : DateTime.Parse((string)row.assigned_at),
        CompletedAt = row.completed_at is null ? null : DateTime.Parse((string)row.completed_at),
        ResultSummary = row.result_summary is null ? null : (string)row.result_summary,
        Confidence = row.confidence is null ? null : (float?)(double)row.confidence,
        Error = row.error is null ? null : (string)row.error,
        CreatedAt = DateTime.Parse((string)row.created_at)
    };

    private static ResearchFile MapFile(dynamic row) => new()
    {
        Id = (string)row.id,
        SessionId = (string)row.session_id,
        Filename = (string)row.filename,
        ContentType = (string)row.content_type,
        FilePath = (string)row.file_path,
        SizeBytes = (long)row.size_bytes,
        CreatedAt = DateTime.Parse((string)row.created_at)
    };
}
