namespace Darci.Research;

/// <summary>
/// Persistent store for research sessions, results, agent jobs, and file artifacts.
/// </summary>
public interface IResearchStore
{
    Task InitializeAsync();

    Task<ResearchSession> CreateSessionAsync(string title, string description, string createdBy = "DARCI", string[]? tags = null);
    Task<ResearchSession?> GetSessionAsync(string sessionId);
    Task<IReadOnlyList<ResearchSession>> GetSessionsAsync(string? status = null, int limit = 50);
    Task<bool> CompleteSessionAsync(string sessionId, string status = "completed");

    Task<ResearchResult> AddResultAsync(string sessionId, string source, string content, string resultType = "text", string[]? tags = null, float relevanceScore = 0f);
    Task<IReadOnlyList<ResearchResult>> GetResultsAsync(string sessionId, string? resultType = null);
    Task<IReadOnlyList<ResearchResult>> SearchResultsAsync(string query, int limit = 20);

    Task<ResearchAgentJob> CreateAgentJobAsync(string sessionId, string subQuestion, string agentType, string status = "queued");
    Task<ResearchAgentJob?> GetAgentJobAsync(string jobId);
    Task<IReadOnlyList<ResearchAgentJob>> GetAgentJobsAsync(string sessionId);
    Task<bool> UpdateAgentJobAsync(
        string jobId,
        string status,
        string? resultSummary = null,
        float? confidence = null,
        string? error = null,
        DateTime? assignedAt = null,
        DateTime? completedAt = null);

    Task<ResearchFile> RegisterFileAsync(string sessionId, string filename, string contentType, string filePath, long sizeBytes);
    Task<IReadOnlyList<ResearchFile>> GetFilesAsync(string? sessionId = null);
    Task<ResearchFile?> GetFileAsync(string fileId);
    Task<bool> DeleteFileAsync(string fileId);

    Task<SessionSummary?> GetSessionSummaryAsync(string sessionId);
}
