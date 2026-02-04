using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Memory;

/// <summary>
/// SQLite-based memory store with semantic search via embeddings
/// </summary>
public class MemoryStore : IMemoryStore
{
    private readonly ILogger<MemoryStore> _logger;
    private readonly string _connectionString;
    private readonly Func<string, Task<List<float>>> _getEmbedding;
    
    public MemoryStore(
        ILogger<MemoryStore> logger,
        string connectionString,
        Func<string, Task<List<float>>> getEmbedding)
    {
        _logger = logger;
        _connectionString = connectionString;
        _getEmbedding = getEmbedding;
        
        InitializeDatabase();
    }
    
    public async Task Store(string content, string[] tags)
    {
        var embedding = await _getEmbedding(content);
        var embeddingJson = JsonSerializer.Serialize(embedding);
        var tagsJson = JsonSerializer.Serialize(tags);
        
        using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO Memories (Content, Tags, Embedding, CreatedAt, LastAccessedAt, AccessCount, Importance)
            VALUES (@Content, @Tags, @Embedding, @CreatedAt, @CreatedAt, 0, 0.5)",
            new
            {
                Content = content,
                Tags = tagsJson,
                Embedding = embeddingJson,
                CreatedAt = DateTime.UtcNow.ToString("o")
            });
        
        _logger.LogDebug("Stored memory: {Preview}...", 
            content.Length > 50 ? content[..50] : content);
    }
    
    public async Task<List<MemoryEntry>> Recall(string query, int limit = 5)
    {
        var queryEmbedding = await _getEmbedding(query);
        
        using var conn = new SqliteConnection(_connectionString);
        var allMemories = await conn.QueryAsync<MemoryRecord>(
            "SELECT * FROM Memories ORDER BY CreatedAt DESC LIMIT 1000");
        
        var scored = new List<(MemoryEntry Entry, float Score)>();
        
        foreach (var record in allMemories)
        {
            var embedding = JsonSerializer.Deserialize<List<float>>(record.Embedding ?? "[]") ?? new();
            var similarity = CosineSimilarity(queryEmbedding, embedding);
            
            // Boost recent memories slightly
            var ageHours = (DateTime.UtcNow - DateTime.Parse(record.CreatedAt)).TotalHours;
            var recencyBoost = Math.Max(0, 1 - (ageHours / 168)); // Decays over a week
            
            var finalScore = similarity * 0.8f + (float)recencyBoost * 0.2f;
            
            var entry = new MemoryEntry
            {
                Id = record.Id,
                Content = record.Content,
                Tags = JsonSerializer.Deserialize<string[]>(record.Tags ?? "[]") ?? Array.Empty<string>(),
                CreatedAt = DateTime.Parse(record.CreatedAt),
                LastAccessedAt = DateTime.Parse(record.LastAccessedAt),
                AccessCount = record.AccessCount,
                Importance = record.Importance,
                RelevanceScore = finalScore
            };
            
            scored.Add((entry, finalScore));
        }
        
        var results = scored
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .Select(s => s.Entry)
            .ToList();
        
        // Update access counts
        foreach (var entry in results)
        {
            await conn.ExecuteAsync(@"
                UPDATE Memories 
                SET AccessCount = AccessCount + 1, 
                    LastAccessedAt = @Now 
                WHERE Id = @Id",
                new { Id = entry.Id, Now = DateTime.UtcNow.ToString("o") });
        }
        
        _logger.LogDebug("Recalled {Count} memories for query: {Query}", results.Count, query);
        
        return results;
    }
    
    public async Task Consolidate()
    {
        using var conn = new SqliteConnection(_connectionString);
        
        // 1. Decay old, unaccessed memories
        var oldCutoff = DateTime.UtcNow.AddDays(-30).ToString("o");
        await conn.ExecuteAsync(@"
            UPDATE Memories 
            SET Importance = Importance * 0.95 
            WHERE LastAccessedAt < @Cutoff AND Importance > 0.1",
            new { Cutoff = oldCutoff });
        
        // 2. Boost frequently accessed memories
        await conn.ExecuteAsync(@"
            UPDATE Memories 
            SET Importance = MIN(Importance * 1.05, 1.0) 
            WHERE AccessCount > 5 AND Importance < 0.9");
        
        // 3. Remove very low importance, old memories
        var deleteCutoff = DateTime.UtcNow.AddDays(-90).ToString("o");
        var deleted = await conn.ExecuteAsync(@"
            DELETE FROM Memories 
            WHERE Importance < 0.1 AND CreatedAt < @Cutoff",
            new { Cutoff = deleteCutoff });
        
        if (deleted > 0)
        {
            _logger.LogInformation("Consolidated memories: removed {Count} old entries", deleted);
        }
    }
    
    public async Task<int> GetPendingConsolidationCount()
    {
        using var conn = new SqliteConnection(_connectionString);
        
        // Count memories that haven't been consolidated recently
        var cutoff = DateTime.UtcNow.AddHours(-24).ToString("o");
        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM Memories 
            WHERE LastAccessedAt < @Cutoff",
            new { Cutoff = cutoff });
    }
    
    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS Memories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Content TEXT NOT NULL,
                Tags TEXT NOT NULL DEFAULT '[]',
                Embedding TEXT,
                CreatedAt TEXT NOT NULL,
                LastAccessedAt TEXT NOT NULL,
                AccessCount INTEGER DEFAULT 0,
                Importance REAL DEFAULT 0.5
            );
            
            CREATE INDEX IF NOT EXISTS idx_memories_created ON Memories(CreatedAt);
            CREATE INDEX IF NOT EXISTS idx_memories_importance ON Memories(Importance);
        ");
        
        _logger.LogInformation("Memory database initialized");
    }
    
    private float CosineSimilarity(List<float> a, List<float> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        
        float dot = 0, magA = 0, magB = 0;
        var len = Math.Min(a.Count, b.Count);
        
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        
        if (magA == 0 || magB == 0) return 0;
        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }
    
    private class MemoryRecord
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public string Tags { get; set; } = "[]";
        public string? Embedding { get; set; }
        public string CreatedAt { get; set; } = "";
        public string LastAccessedAt { get; set; } = "";
        public int AccessCount { get; set; }
        public float Importance { get; set; }
    }
}
