using Darci.Memory.Graph;
using Darci.Memory.Graph.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests.Contract;

/// <summary>
/// P2c.1 — the memory broker. Nodes reach the knowledge graph only through this, with per-request scope
/// enforcement, so "no node touches a resource directly" (doc P3) is enforced rather than merely intended.
/// </summary>
public class MemoryBrokerTests
{
    /// <summary>Records which graph methods were reached, so a denied call can be shown to never touch the store.</summary>
    private sealed class RecordingGraph : IKnowledgeGraph
    {
        public List<string> Calls { get; } = new();

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<KgEntity> UpsertEntityAsync(string name, string entityType, string domain, string? description = null,
            string[]? aliases = null, float[]? embedding = null, CancellationToken ct = default)
        {
            Calls.Add(nameof(UpsertEntityAsync));
            return Task.FromResult(new KgEntity { Id = "e1", Name = name, EntityType = entityType, Domain = domain });
        }

        public Task<KgEntity?> GetEntityAsync(string id, CancellationToken ct = default)
        {
            Calls.Add(nameof(GetEntityAsync));
            return Task.FromResult<KgEntity?>(new KgEntity { Id = id, Name = "x" });
        }

        public Task<KgEntity?> FindEntityByNameAsync(string name, CancellationToken ct = default)
        {
            Calls.Add(nameof(FindEntityByNameAsync));
            return Task.FromResult<KgEntity?>(null);
        }

        public Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(string query, string? domain = null, int limit = 20, CancellationToken ct = default)
        {
            Calls.Add(nameof(SearchEntitiesAsync));
            return Task.FromResult<IReadOnlyList<KgEntity>>(new[] { new KgEntity { Id = "e1", Name = query } });
        }

        public Task<IReadOnlyList<KgEntity>> GetEntitiesByDomainAsync(string domain, int limit = 100, CancellationToken ct = default)
        {
            Calls.Add(nameof(GetEntitiesByDomainAsync));
            return Task.FromResult<IReadOnlyList<KgEntity>>(Array.Empty<KgEntity>());
        }

        public Task<KgRelation> UpsertRelationAsync(string fromEntityId, string toEntityId, string relationType,
            float weight = 1, float confidence = 0.5f, string[]? evidenceIds = null, CancellationToken ct = default)
        {
            Calls.Add(nameof(UpsertRelationAsync));
            return Task.FromResult(new KgRelation { Id = "r1", FromEntityId = fromEntityId, ToEntityId = toEntityId, RelationType = relationType });
        }

        public Task<IReadOnlyList<KgRelation>> GetRelationsAsync(string entityId, string? relationType = null, bool incoming = false, CancellationToken ct = default)
        {
            Calls.Add(nameof(GetRelationsAsync));
            return Task.FromResult<IReadOnlyList<KgRelation>>(Array.Empty<KgRelation>());
        }

        public Task<GraphNeighbours> GetNeighboursAsync(string entityId, int depth = 1, string? relationTypeFilter = null, CancellationToken ct = default)
        {
            Calls.Add(nameof(GetNeighboursAsync));
            return Task.FromResult(new GraphNeighbours());
        }

        public Task<GraphPath> FindPathAsync(string fromEntityId, string toEntityId, int maxHops = 5, CancellationToken ct = default)
        {
            Calls.Add(nameof(FindPathAsync));
            return Task.FromResult(new GraphPath());
        }

        public Task<IReadOnlyList<(KgEntity Entity, float Score)>> SemanticSearchAsync(float[] queryEmbedding, int limit = 10, CancellationToken ct = default)
        {
            Calls.Add(nameof(SemanticSearchAsync));
            return Task.FromResult<IReadOnlyList<(KgEntity, float)>>(Array.Empty<(KgEntity, float)>());
        }

        public Task IngestMemoryAsync(string memoryContent, string[] tags, Func<string, Task<List<float>>> getEmbedding,
            Func<string, Task<string>> llmExtract, CancellationToken ct = default)
        {
            Calls.Add(nameof(IngestMemoryAsync));
            return Task.CompletedTask;
        }
    }

    private static (MemoryBroker Broker, RecordingGraph Graph) Broker()
    {
        var graph = new RecordingGraph();
        return (new MemoryBroker(graph, NullLogger<MemoryBroker>.Instance), graph);
    }

    private static MemoryAccess ReadOnly(string node = "darci.innovation") =>
        MemoryAccess.ForNode(node, new[] { MemoryScopes.ReadKnowledge });

    private static MemoryAccess ReadWrite(string node = "darci.knowledge") =>
        MemoryAccess.ForNode(node, new[] { MemoryScopes.ReadKnowledge, MemoryScopes.WriteKnowledge });

    // ── reads ──

    [Fact]
    public async Task AReadScopeAllowsReads()
    {
        var (broker, graph) = Broker();

        var results = await broker.SearchEntitiesAsync(ReadOnly(), "myoelectric");

        Assert.Single(results);
        Assert.Equal(new[] { "SearchEntitiesAsync" }, graph.Calls);
    }

    [Fact]
    public async Task EveryReadPathRequiresReadKnowledge()
    {
        var (broker, graph) = Broker();
        var none = MemoryAccess.ForNode("darci.rogue", Array.Empty<string>());

        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() => broker.SearchEntitiesAsync(none, "q"));
        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() => broker.GetEntityAsync(none, "e1"));
        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() => broker.FindEntityByNameAsync(none, "n"));
        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() => broker.GetEntitiesByDomainAsync(none, "d"));
        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() => broker.SemanticSearchAsync(none, new float[] { 1f }));
        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() => broker.GetNeighboursAsync(none, "e1"));
        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() => broker.GetRelationsAsync(none, "e1"));
        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() => broker.FindPathAsync(none, "a", "b"));

        // THE point: a denied call never reaches the store at all.
        Assert.Empty(graph.Calls);
    }

    // ── writes ──

    [Fact]
    public async Task WritesRequireWriteKnowledge_NotJustRead()
    {
        var (broker, graph) = Broker();

        var ex = await Assert.ThrowsAsync<MemoryScopeDeniedException>(() =>
            broker.UpsertEntityAsync(ReadOnly(), "thing", "Concept", "general"));

        Assert.Equal(MemoryScopes.WriteKnowledge, ex.RequiredScope);
        Assert.Equal("darci.innovation", ex.CallerId);
        Assert.Empty(graph.Calls);

        await Assert.ThrowsAsync<MemoryScopeDeniedException>(() =>
            broker.UpsertRelationAsync(ReadOnly(), "a", "b", "relates_to"));
        Assert.Empty(graph.Calls);
    }

    [Fact]
    public async Task AWriteScopeAllowsWrites()
    {
        var (broker, graph) = Broker();

        await broker.UpsertEntityAsync(ReadWrite(), "thing", "Concept", "general");
        await broker.UpsertRelationAsync(ReadWrite(), "a", "b", "relates_to");

        Assert.Equal(new[] { "UpsertEntityAsync", "UpsertRelationAsync" }, graph.Calls);
    }

    [Fact]
    public async Task TheDenialMessageNamesTheCallerTheScopeAndTheFix()
    {
        var (broker, _) = Broker();
        var ex = await Assert.ThrowsAsync<MemoryScopeDeniedException>(() =>
            broker.UpsertEntityAsync(ReadOnly("darci.coding"), "n", "t", "d"));

        Assert.Contains("darci.coding", ex.Message);
        Assert.Contains(MemoryScopes.WriteKnowledge, ex.Message);
        Assert.Contains("requires.memory_scopes", ex.Message);   // tells you exactly where to fix it
    }

    // ── the core's own access (doc §3) ──

    [Fact]
    public async Task TheCoreHasFullAccess_BecauseSection3MakesItTheOwnerOfTheGraph()
    {
        // The broker mediates NODE access; it is not a wall between the core and its own store.
        var (broker, graph) = Broker();

        await broker.SearchEntitiesAsync(MemoryAccess.Core, "q");
        await broker.UpsertEntityAsync(MemoryAccess.Core, "n", "t", "d");

        Assert.Equal(2, graph.Calls.Count);
        Assert.All(MemoryScopes.All, s => Assert.True(MemoryAccess.Core.Allows(s)));
    }

    // ── scopes line up with what the manifests already declare ──

    [Fact]
    public void ManifestScopeStrings_MatchTheBrokersConstants()
    {
        // Phase 1 populated requires.memory_scopes in the real manifests; those strings must be the ones the
        // broker enforces, or a node would be granted nothing it asked for.
        Assert.True(MemoryScopes.IsKnown("read:knowledge"));
        Assert.True(MemoryScopes.IsKnown("write:knowledge"));
        Assert.True(MemoryScopes.IsKnown("read:workspace"));
        Assert.False(MemoryScopes.IsKnown("read:everything"));
    }

    [Fact]
    public void AccessIsBuiltFromDeclaredScopes_AndGrantsNothingElse()
    {
        var access = MemoryAccess.ForNode("darci.innovation", new[] { MemoryScopes.ReadKnowledge });
        Assert.True(access.Allows(MemoryScopes.ReadKnowledge));
        Assert.False(access.Allows(MemoryScopes.WriteKnowledge));
        Assert.False(access.Allows(MemoryScopes.ReadWorkspace));
    }
}
