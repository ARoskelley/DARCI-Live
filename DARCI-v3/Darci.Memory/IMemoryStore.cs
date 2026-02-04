namespace Darci.Memory;

/// <summary>
/// DARCI's memory system - stores and retrieves memories with semantic search
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Store a memory with optional tags
    /// </summary>
    Task Store(string content, string[] tags);
    
    /// <summary>
    /// Recall memories relevant to a query using semantic search
    /// </summary>
    Task<List<MemoryEntry>> Recall(string query, int limit = 5);
    
    /// <summary>
    /// Run memory consolidation (decay, linking, cleanup)
    /// </summary>
    Task Consolidate();
    
    /// <summary>
    /// Get count of memories needing consolidation
    /// </summary>
    Task<int> GetPendingConsolidationCount();
}

/// <summary>
/// A single memory entry
/// </summary>
public class MemoryEntry
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public string[] Tags { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
    public float Importance { get; set; } = 0.5f;
    public float RelevanceScore { get; set; } // Set during recall
}
