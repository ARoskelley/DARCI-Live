#nullable enable

using Darci.Memory.Graph.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Memory.Graph;

/// <summary>
/// Memory scopes (doc §6.1). A node declares the narrowest set it needs in its <c>darci-node.json</c>
/// <c>requires.memory_scopes</c>, and the broker enforces them per request.
/// </summary>
public static class MemoryScopes
{
    /// <summary>Read entities/relations from the knowledge graph.</summary>
    public const string ReadKnowledge = "read:knowledge";

    /// <summary>Create or update entities/relations. Attributed to the writing node.</summary>
    public const string WriteKnowledge = "write:knowledge";

    /// <summary>Read document-shaped entities.</summary>
    public const string ReadDocuments = "read:documents";

    /// <summary>Read the coding workspace projection of the graph.</summary>
    public const string ReadWorkspace = "read:workspace";

    public static readonly string[] All = { ReadKnowledge, WriteKnowledge, ReadDocuments, ReadWorkspace };

    public static bool IsKnown(string? scope) =>
        scope is not null && Array.Exists(All, s => string.Equals(s, scope, StringComparison.Ordinal));
}

/// <summary>
/// Who is asking, and what they declared they need. Built from a node's manifest, so the granted scopes are
/// exactly the reviewed ones.
/// </summary>
public sealed record MemoryAccess(string CallerId, IReadOnlySet<string> Scopes)
{
    public bool Allows(string scope) => Scopes.Contains(scope);

    public static MemoryAccess ForNode(string nodeId, IEnumerable<string> scopes) =>
        new(nodeId, new HashSet<string>(scopes, StringComparer.Ordinal));

    /// <summary>
    /// The CORE's own access. Doc §3 makes the core the component that owns the knowledge graph, so core
    /// services (MemoryStore, ConfidenceTracker, the KG REST endpoints) legitimately hold full access — the
    /// broker exists to mediate NODE access, not to wall the core off from itself.
    /// </summary>
    public static MemoryAccess Core { get; } = new("darci.core", new HashSet<string>(MemoryScopes.All, StringComparer.Ordinal));
}

/// <summary>Thrown when a caller requests memory outside its declared scopes (doc §6.1 PERMISSION_DENIED).</summary>
public sealed class MemoryScopeDeniedException : Exception
{
    public MemoryScopeDeniedException(string callerId, string requiredScope, IEnumerable<string> granted)
        : base($"Memory access denied: '{callerId}' needs scope '{requiredScope}' but declared only " +
               $"[{string.Join(", ", granted)}]. Add it to the node's manifest requires.memory_scopes if it is genuinely needed.")
    {
        CallerId = callerId;
        RequiredScope = requiredScope;
    }

    public string CallerId { get; }
    public string RequiredScope { get; }
}

/// <summary>
/// THE MEMORY BROKER (doc §6.1). Nodes reach the knowledge graph through this and never hold the store
/// directly — "no node touches a resource directly" (P3).
///
/// <para>Scopes are enforced per request, and a denial is LOGGED: per §6.1 that log is how you notice a
/// misbehaving or compromised node. Writes are attributed to the caller so provenance survives.</para>
///
/// <para>Deliberately NOT brokered: the core's own lifecycle and trust state — InnovatedKnowledgeStore,
/// ProposalStore, ValidationCampaignStore, GapStore, NodePacketStore. Those are the core's bookkeeping and
/// its §0a trust ledger; putting them behind an indirection would add ceremony with no safety gain.</para>
/// </summary>
public interface IMemoryBroker
{
    Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(
        MemoryAccess access, string query, string? domain = null, int limit = 20, CancellationToken ct = default);

    Task<KgEntity?> GetEntityAsync(MemoryAccess access, string id, CancellationToken ct = default);

    Task<KgEntity?> FindEntityByNameAsync(MemoryAccess access, string name, CancellationToken ct = default);

    Task<IReadOnlyList<KgEntity>> GetEntitiesByDomainAsync(
        MemoryAccess access, string domain, int limit = 100, CancellationToken ct = default);

    Task<IReadOnlyList<(KgEntity Entity, float Score)>> SemanticSearchAsync(
        MemoryAccess access, float[] queryEmbedding, int limit = 10, CancellationToken ct = default);

    Task<GraphNeighbours> GetNeighboursAsync(
        MemoryAccess access, string entityId, int depth = 1, string? relationTypeFilter = null, CancellationToken ct = default);

    Task<IReadOnlyList<KgRelation>> GetRelationsAsync(
        MemoryAccess access, string entityId, string? relationType = null, bool incoming = false, CancellationToken ct = default);

    Task<GraphPath> FindPathAsync(
        MemoryAccess access, string fromEntityId, string toEntityId, int maxHops = 5, CancellationToken ct = default);

    Task<KgEntity> UpsertEntityAsync(
        MemoryAccess access, string name, string entityType, string domain, string? description = null,
        string[]? aliases = null, float[]? embedding = null, CancellationToken ct = default);

    Task<KgRelation> UpsertRelationAsync(
        MemoryAccess access, string fromEntityId, string toEntityId, string relationType,
        float weight = 1.0f, float confidence = 0.5f, string[]? evidenceIds = null, CancellationToken ct = default);
}

/// <summary>Scope-enforcing broker over the concrete <see cref="IKnowledgeGraph"/>.</summary>
public sealed class MemoryBroker : IMemoryBroker
{
    private readonly IKnowledgeGraph _graph;
    private readonly ILogger<MemoryBroker> _logger;

    public MemoryBroker(IKnowledgeGraph graph, ILogger<MemoryBroker> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    public Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(
        MemoryAccess access, string query, string? domain = null, int limit = 20, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.ReadKnowledge);
        return _graph.SearchEntitiesAsync(query, domain, limit, ct);
    }

    public Task<KgEntity?> GetEntityAsync(MemoryAccess access, string id, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.ReadKnowledge);
        return _graph.GetEntityAsync(id, ct);
    }

    public Task<KgEntity?> FindEntityByNameAsync(MemoryAccess access, string name, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.ReadKnowledge);
        return _graph.FindEntityByNameAsync(name, ct);
    }

    public Task<IReadOnlyList<KgEntity>> GetEntitiesByDomainAsync(
        MemoryAccess access, string domain, int limit = 100, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.ReadKnowledge);
        return _graph.GetEntitiesByDomainAsync(domain, limit, ct);
    }

    public Task<IReadOnlyList<(KgEntity Entity, float Score)>> SemanticSearchAsync(
        MemoryAccess access, float[] queryEmbedding, int limit = 10, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.ReadKnowledge);
        return _graph.SemanticSearchAsync(queryEmbedding, limit, ct);
    }

    public Task<GraphNeighbours> GetNeighboursAsync(
        MemoryAccess access, string entityId, int depth = 1, string? relationTypeFilter = null, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.ReadKnowledge);
        return _graph.GetNeighboursAsync(entityId, depth, relationTypeFilter, ct);
    }

    public Task<IReadOnlyList<KgRelation>> GetRelationsAsync(
        MemoryAccess access, string entityId, string? relationType = null, bool incoming = false, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.ReadKnowledge);
        return _graph.GetRelationsAsync(entityId, relationType, incoming, ct);
    }

    public Task<GraphPath> FindPathAsync(
        MemoryAccess access, string fromEntityId, string toEntityId, int maxHops = 5, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.ReadKnowledge);
        return _graph.FindPathAsync(fromEntityId, toEntityId, maxHops, ct);
    }

    public Task<KgEntity> UpsertEntityAsync(
        MemoryAccess access, string name, string entityType, string domain, string? description = null,
        string[]? aliases = null, float[]? embedding = null, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.WriteKnowledge);
        _logger.LogDebug("Memory write by {Caller}: entity '{Name}' ({Type}/{Domain}).", access.CallerId, name, entityType, domain);
        return _graph.UpsertEntityAsync(name, entityType, domain, description, aliases, embedding, ct);
    }

    public Task<KgRelation> UpsertRelationAsync(
        MemoryAccess access, string fromEntityId, string toEntityId, string relationType,
        float weight = 1.0f, float confidence = 0.5f, string[]? evidenceIds = null, CancellationToken ct = default)
    {
        Require(access, MemoryScopes.WriteKnowledge);
        _logger.LogDebug("Memory write by {Caller}: relation {From} -{Type}-> {To}.",
            access.CallerId, fromEntityId, relationType, toEntityId);
        return _graph.UpsertRelationAsync(fromEntityId, toEntityId, relationType, weight, confidence, evidenceIds, ct);
    }

    /// <summary>Enforce a scope. A denial is logged as a WARNING — §6.1: that log is how a misbehaving or
    /// compromised node becomes visible.</summary>
    private void Require(MemoryAccess access, string scope)
    {
        if (access.Allows(scope)) return;

        _logger.LogWarning(
            "MEMORY ACCESS DENIED: '{Caller}' requested scope '{Scope}' but declared only [{Granted}].",
            access.CallerId, scope, string.Join(", ", access.Scopes));
        throw new MemoryScopeDeniedException(access.CallerId, scope, access.Scopes);
    }
}
