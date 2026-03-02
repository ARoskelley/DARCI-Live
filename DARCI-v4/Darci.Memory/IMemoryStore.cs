using Darci.Shared;

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
