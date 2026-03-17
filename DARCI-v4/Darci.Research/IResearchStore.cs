namespace Darci.Research;

/// <summary>
/// Persistent store for research sessions, results, and file artifacts.
///
/// Used by:
/// - DARCI's Research action to log what she investigates
/// - External research agents that write results for DARCI to consume
/// - The mobile app to browse completed research files
/// </summary>
public interface IResearchStore
{
    Task InitializeAsync();

    // ─── Sessions ────────────────────────────────────────────────────────────

    Task<ResearchSession> CreateSessionAsync(string title, string description, string createdBy = "DARCI", string[]? tags = null);
    Task<ResearchSession?> GetSessionAsync(string sessionId);
    Task<IReadOnlyList<ResearchSession>> GetSessionsAsync(string? status = null, int limit = 50);
    Task<bool> CompleteSessionAsync(string sessionId, string status = "completed");

    // ─── Results ─────────────────────────────────────────────────────────────

    Task<ResearchResult> AddResultAsync(string sessionId, string source, string content, string resultType = "text", string[]? tags = null, float relevanceScore = 0f);
    Task<IReadOnlyList<ResearchResult>> GetResultsAsync(string sessionId, string? resultType = null);
    Task<IReadOnlyList<ResearchResult>> SearchResultsAsync(string query, int limit = 20);

    // ─── Files ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a file artifact in the database.
    /// The caller is responsible for writing the actual file to <paramref name="filePath"/>.
    /// </summary>
    Task<ResearchFile> RegisterFileAsync(string sessionId, string filename, string contentType, string filePath, long sizeBytes);

    Task<IReadOnlyList<ResearchFile>> GetFilesAsync(string? sessionId = null);
    Task<ResearchFile?> GetFileAsync(string fileId);
    Task<bool> DeleteFileAsync(string fileId);

    // ─── Summary ─────────────────────────────────────────────────────────────

    Task<SessionSummary?> GetSessionSummaryAsync(string sessionId);
}
