#nullable enable

using Darci.Memory.Graph.Models;

namespace Darci.Memory.Graph;

public interface IKnowledgeGraph
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<KgEntity> UpsertEntityAsync(
        string name,
        string entityType,
        string domain,
        string? description = null,
        string[]? aliases = null,
        float[]? embedding = null,
        CancellationToken ct = default);

    Task<KgEntity?> GetEntityAsync(string id, CancellationToken ct = default);

    Task<KgEntity?> FindEntityByNameAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(
        string query,
        string? domain = null,
        int limit = 20,
        CancellationToken ct = default);

    Task<IReadOnlyList<KgEntity>> GetEntitiesByDomainAsync(
        string domain,
        int limit = 100,
        CancellationToken ct = default);

    Task<KgRelation> UpsertRelationAsync(
        string fromEntityId,
        string toEntityId,
        string relationType,
        float weight = 1.0f,
        float confidence = 0.5f,
        string[]? evidenceIds = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<KgRelation>> GetRelationsAsync(
        string entityId,
        string? relationType = null,
        bool incoming = false,
        CancellationToken ct = default);

    Task<GraphNeighbours> GetNeighboursAsync(
        string entityId,
        int depth = 1,
        string? relationTypeFilter = null,
        CancellationToken ct = default);

    Task<GraphPath> FindPathAsync(
        string fromEntityId,
        string toEntityId,
        int maxHops = 5,
        CancellationToken ct = default);

    Task<IReadOnlyList<(KgEntity Entity, float Score)>> SemanticSearchAsync(
        float[] queryEmbedding,
        int limit = 10,
        CancellationToken ct = default);

    Task IngestMemoryAsync(
        string memoryContent,
        string[] tags,
        Func<string, Task<List<float>>> getEmbedding,
        Func<string, Task<string>> llmExtract,
        CancellationToken ct = default);
}
