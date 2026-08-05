#nullable enable

using Dapper;
using Darci.Memory.Confidence;
using Darci.Memory.Graph;
using Darci.Memory.Graph.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using static Darci.Memory.Graph.Neo4jWrite;

namespace Darci.Research.Agents.Tests.Contract;

/// <summary>
/// P2d.2 — DUAL-READ PARITY. The Neo4j swap is a backing-store change ONLY, so the proof obligation is
/// narrow and concrete: run the same operation against the SQLite graph and the Neo4j graph, and compare
/// what comes back. Anything these tests do not compare is a behavior change nobody authorized.
///
/// <para>These are integration tests against a live Neo4j. If none is reachable they no-op rather than fail,
/// so a machine without Neo4j can still run the suite — but note the tradeoff honestly: a skipped parity
/// test proves NOTHING. <see cref="Neo4jAvailability.Reason"/> records why it skipped, and the cutover in
/// P2d.3 is only justified by a run where these actually executed.</para>
/// </summary>
[Collection(Neo4jCollection.Name)]
public sealed class Neo4jParityTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _sqliteConn;
    private KnowledgeGraph _sqlite = null!;
    private Neo4jKnowledgeGraph? _neo4j;
    private string _label = "";

    public Neo4jParityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-parity-{Guid.NewGuid():N}.db");
        _sqliteConn = $"Data Source={_dbPath}";
    }

    public async Task InitializeAsync()
    {
        _sqlite = new KnowledgeGraph(_sqliteConn, NullLogger<KnowledgeGraph>.Instance);
        await _sqlite.InitializeAsync();

        // Entity confidence is derived from confidence_claims, so the claims schema must exist for BOTH
        // backends — that cross-store read is precisely what the Neo4j implementation has to preserve.
        await new ConfidenceTracker(_sqliteConn, _sqlite, NullLogger<ConfidenceTracker>.Instance)
            .InitializeAsync();

        if (!Neo4jAvailability.IsAvailable) return;

        _label = $"p{Guid.NewGuid():N}";
        _neo4j = new Neo4jKnowledgeGraph(
            Neo4jAvailability.Options with { ClaimsConnectionString = _sqliteConn },
            NullLogger<Neo4jKnowledgeGraph>.Instance);
        await _neo4j.InitializeAsync();

        // SQLite gets a brand-new file per test; Neo4j is a long-lived shared server. Without matching that,
        // entities from earlier tests leak into every unfiltered query (domain listings, semantic search)
        // and the two backends are no longer being asked the same question. Hence the wipe — and hence the
        // DARCI_NEO4J_TEST_WIPE opt-in gating the whole suite, because a test that silently empties a graph
        // someone cared about would be far worse than a test that does not run.
        await Neo4jAvailability.WipeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_neo4j is not null)
        {
            await _neo4j.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private string N(string name) => $"{_label}_{name}";

    // ── entities ──

    [Fact]
    public async Task UpsertEntity_ReturnsTheSameEntityShapeOnBothBackends()
    {
        if (_neo4j is null) return;

        var a = await _sqlite.UpsertEntityAsync(N("EMG Sensor"), "Device", "prosthetics", "Reads muscle signals.", new[] { "electromyography sensor" });
        var b = await _neo4j.UpsertEntityAsync(N("EMG Sensor"), "Device", "prosthetics", "Reads muscle signals.", new[] { "electromyography sensor" });

        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.EntityType, b.EntityType);
        Assert.Equal(a.Domain, b.Domain);
        Assert.Equal(a.Description, b.Description);
        Assert.Equal(a.Aliases, b.Aliases);
        Assert.Equal(a.Confidence, b.Confidence, 4);
        Assert.Equal(a.SourceCount, b.SourceCount);
    }

    [Fact]
    public async Task UpsertEntity_IsIdempotentByNameAndMergesAliasesOnBothBackends()
    {
        if (_neo4j is null) return;

        await _sqlite.UpsertEntityAsync(N("Grip Loop"), "Concept", "prosthetics", "v1", new[] { "grasp loop" });
        await _neo4j.UpsertEntityAsync(N("Grip Loop"), "Concept", "prosthetics", "v1", new[] { "grasp loop" });

        var a = await _sqlite.UpsertEntityAsync(N("Grip Loop"), "Concept", "prosthetics", "v2", new[] { "closed grip" });
        var b = await _neo4j.UpsertEntityAsync(N("Grip Loop"), "Concept", "prosthetics", "v2", new[] { "closed grip" });

        Assert.Equal(a.Id, (await _sqlite.FindEntityByNameAsync(N("Grip Loop")))!.Id);
        Assert.Equal(b.Id, (await _neo4j.FindEntityByNameAsync(N("Grip Loop")))!.Id);
        Assert.Equal(a.Aliases, b.Aliases);
        Assert.Equal(2, b.Aliases.Length);
        Assert.Equal(a.Description, b.Description);
    }

    [Fact]
    public async Task FindEntityByName_IsCaseInsensitiveOnBothBackends()
    {
        if (_neo4j is null) return;

        await _sqlite.UpsertEntityAsync(N("Servo Actuator"), "Device", "prosthetics");
        await _neo4j.UpsertEntityAsync(N("Servo Actuator"), "Device", "prosthetics");

        var a = await _sqlite.FindEntityByNameAsync(N("Servo Actuator").ToUpperInvariant());
        var b = await _neo4j.FindEntityByNameAsync(N("Servo Actuator").ToUpperInvariant());

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Name, b!.Name);
    }

    [Fact]
    public async Task SearchEntities_MatchesNameAndAliasAndRanksIdenticallyOnBothBackends()
    {
        if (_neo4j is null) return;

        foreach (var g in Both())
        {
            await g.UpsertEntityAsync(N("Torque"), "Concept", "mechanics");                          // exact-ish
            await g.UpsertEntityAsync(N("Torque Sensor"), "Device", "mechanics");                    // name contains
            await g.UpsertEntityAsync(N("Load Cell"), "Device", "mechanics", null, new[] { "torque probe" }); // alias contains
            await g.UpsertEntityAsync(N("Battery"), "Device", "mechanics");                          // no match
        }

        var a = await _sqlite.SearchEntitiesAsync("torque", limit: 10);
        var b = await _neo4j.SearchEntitiesAsync("torque", limit: 10);

        Assert.Equal(a.Select(e => e.Name), b.Select(e => e.Name));
        Assert.DoesNotContain(b, e => e.Name == N("Battery"));
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public async Task SearchEntities_AppliesTheDomainFilterIdenticallyOnBothBackends()
    {
        if (_neo4j is null) return;

        foreach (var g in Both())
        {
            await g.UpsertEntityAsync(N("Signal Filter"), "Concept", "prosthetics");
            await g.UpsertEntityAsync(N("Signal Router"), "Concept", "networking");
        }

        var a = await _sqlite.SearchEntitiesAsync("signal", domain: "networking", limit: 10);
        var b = await _neo4j.SearchEntitiesAsync("signal", domain: "networking", limit: 10);

        Assert.Equal(a.Select(e => e.Name), b.Select(e => e.Name));
        Assert.Single(b);
        Assert.Equal(N("Signal Router"), b[0].Name);
    }

    [Fact]
    public async Task GetEntitiesByDomain_ReturnsTheSameSetOnBothBackends()
    {
        if (_neo4j is null) return;

        foreach (var g in Both())
        {
            await g.UpsertEntityAsync(N("Wrist Joint"), "Part", "prosthetics");
            await g.UpsertEntityAsync(N("Elbow Joint"), "Part", "prosthetics");
            await g.UpsertEntityAsync(N("Router"), "Device", "networking");
        }

        var a = await _sqlite.GetEntitiesByDomainAsync("prosthetics", limit: 50);
        var b = await _neo4j.GetEntitiesByDomainAsync("prosthetics", limit: 50);

        Assert.Equal(
            a.Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal),
            b.Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2, b.Count);
    }

    // ── semantic search (in-app cosine on both) ──

    [Fact]
    public async Task SemanticSearch_RanksIdenticallyOnBothBackends()
    {
        if (_neo4j is null) return;

        var near = new[] { 1f, 0f, 0f };
        var mid = new[] { 0.7f, 0.7f, 0f };
        var far = new[] { 0f, 0f, 1f };

        foreach (var g in Both())
        {
            await g.UpsertEntityAsync(N("Near"), "Concept", "general", null, null, near);
            await g.UpsertEntityAsync(N("Mid"), "Concept", "general", null, null, mid);
            await g.UpsertEntityAsync(N("Far"), "Concept", "general", null, null, far);
            await g.UpsertEntityAsync(N("NoVector"), "Concept", "general");
        }

        var a = await _sqlite.SemanticSearchAsync(new[] { 1f, 0f, 0f }, limit: 10);
        var b = await _neo4j.SemanticSearchAsync(new[] { 1f, 0f, 0f }, limit: 10);

        Assert.Equal(a.Select(x => x.Entity.Name), b.Select(x => x.Entity.Name));
        Assert.Equal(N("Near"), b[0].Entity.Name);
        Assert.DoesNotContain(b, x => x.Entity.Name == N("NoVector"));

        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Score, b[i].Score, 4);
        }
    }

    [Fact]
    public async Task SemanticSearch_WithAnEmptyQueryEmbedding_ReturnsNothingOnBothBackends()
    {
        if (_neo4j is null) return;

        await _sqlite.UpsertEntityAsync(N("Anything"), "Concept", "general", null, null, new[] { 1f, 0f });
        await _neo4j.UpsertEntityAsync(N("Anything"), "Concept", "general", null, null, new[] { 1f, 0f });

        Assert.Empty(await _sqlite.SemanticSearchAsync(Array.Empty<float>(), limit: 5));
        Assert.Empty(await _neo4j.SemanticSearchAsync(Array.Empty<float>(), limit: 5));
    }

    [Fact]
    public async Task SemanticSearch_HonoursTheLimitOnBothBackends()
    {
        if (_neo4j is null) return;

        foreach (var g in Both())
        {
            for (var i = 0; i < 5; i++)
            {
                await g.UpsertEntityAsync(N($"Vec{i}"), "Concept", "general", null, null, new[] { 1f - (i * 0.1f), i * 0.1f });
            }
        }

        var a = await _sqlite.SemanticSearchAsync(new[] { 1f, 0f }, limit: 2);
        var b = await _neo4j.SemanticSearchAsync(new[] { 1f, 0f }, limit: 2);

        Assert.Equal(2, b.Count);
        Assert.Equal(a.Select(x => x.Entity.Name), b.Select(x => x.Entity.Name));
    }

    // ── relations ──

    [Fact]
    public async Task Relations_UpsertAndReadBackIdenticallyOnBothBackends()
    {
        if (_neo4j is null) return;

        var (aFrom, aTo) = await SeedPairAsync(_sqlite);
        var (bFrom, bTo) = await SeedPairAsync(_neo4j);

        var ra = await _sqlite.UpsertRelationAsync(aFrom.Id, aTo.Id, "measures", 0.8f, 0.7f, new[] { "ev1" });
        var rb = await _neo4j.UpsertRelationAsync(bFrom.Id, bTo.Id, "measures", 0.8f, 0.7f, new[] { "ev1" });

        Assert.Equal(ra.RelationType, rb.RelationType);
        Assert.Equal(ra.Direction, rb.Direction);
        Assert.Equal(ra.Weight, rb.Weight, 4);
        Assert.Equal(ra.Confidence, rb.Confidence, 4);
        Assert.Equal(ra.EvidenceIds, rb.EvidenceIds);
        Assert.Equal(bFrom.Id, rb.FromEntityId);
        Assert.Equal(bTo.Id, rb.ToEntityId);
    }

    [Fact]
    public async Task Relations_AreIdempotentOnFromToTypeAndMergeEvidenceOnBothBackends()
    {
        if (_neo4j is null) return;

        var (aFrom, aTo) = await SeedPairAsync(_sqlite);
        var (bFrom, bTo) = await SeedPairAsync(_neo4j);

        await _sqlite.UpsertRelationAsync(aFrom.Id, aTo.Id, "measures", evidenceIds: new[] { "ev1" });
        await _neo4j.UpsertRelationAsync(bFrom.Id, bTo.Id, "measures", evidenceIds: new[] { "ev1" });
        await _sqlite.UpsertRelationAsync(aFrom.Id, aTo.Id, "measures", evidenceIds: new[] { "ev2" });
        await _neo4j.UpsertRelationAsync(bFrom.Id, bTo.Id, "measures", evidenceIds: new[] { "ev2" });

        var a = await _sqlite.GetRelationsAsync(aFrom.Id);
        var b = await _neo4j.GetRelationsAsync(bFrom.Id);

        Assert.Single(a);
        Assert.Single(b);
        Assert.Equal(a[0].EvidenceIds.OrderBy(x => x), b[0].EvidenceIds.OrderBy(x => x));
        Assert.Equal(2, b[0].EvidenceIds.Length);
    }

    [Fact]
    public async Task GetRelations_SeparatesIncomingFromOutgoingIdenticallyOnBothBackends()
    {
        if (_neo4j is null) return;

        var (aFrom, aTo) = await SeedPairAsync(_sqlite);
        var (bFrom, bTo) = await SeedPairAsync(_neo4j);

        await _sqlite.UpsertRelationAsync(aFrom.Id, aTo.Id, "measures");
        await _neo4j.UpsertRelationAsync(bFrom.Id, bTo.Id, "measures");

        Assert.Single(await _sqlite.GetRelationsAsync(aFrom.Id));
        Assert.Single(await _neo4j.GetRelationsAsync(bFrom.Id));
        Assert.Empty(await _sqlite.GetRelationsAsync(aFrom.Id, incoming: true));
        Assert.Empty(await _neo4j.GetRelationsAsync(bFrom.Id, incoming: true));
        Assert.Single(await _sqlite.GetRelationsAsync(aTo.Id, incoming: true));
        Assert.Single(await _neo4j.GetRelationsAsync(bTo.Id, incoming: true));
    }

    [Fact]
    public async Task GetRelations_FiltersByRelationTypeOnBothBackends()
    {
        if (_neo4j is null) return;

        var (aFrom, aTo) = await SeedPairAsync(_sqlite);
        var (bFrom, bTo) = await SeedPairAsync(_neo4j);

        await _sqlite.UpsertRelationAsync(aFrom.Id, aTo.Id, "measures");
        await _sqlite.UpsertRelationAsync(aFrom.Id, aTo.Id, "calibrates");
        await _neo4j.UpsertRelationAsync(bFrom.Id, bTo.Id, "measures");
        await _neo4j.UpsertRelationAsync(bFrom.Id, bTo.Id, "calibrates");

        var a = await _sqlite.GetRelationsAsync(aFrom.Id, relationType: "calibrates");
        var b = await _neo4j.GetRelationsAsync(bFrom.Id, relationType: "calibrates");

        Assert.Single(a);
        Assert.Single(b);
        Assert.Equal("calibrates", b[0].RelationType);
    }

    // ── traversal ──

    [Fact]
    public async Task GetNeighbours_ReturnsTheSameNeighbourSetOnBothBackends()
    {
        if (_neo4j is null) return;

        var aIds = await SeedChainAsync(_sqlite);
        var bIds = await SeedChainAsync(_neo4j);

        var a = await _sqlite.GetNeighboursAsync(aIds.Root, depth: 1);
        var b = await _neo4j.GetNeighboursAsync(bIds.Root, depth: 1);

        Assert.Equal(
            a.Entities.Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal),
            b.Entities.Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(a.Depth, b.Depth);
        Assert.Equal(a.RootEntityId, aIds.Root);
        Assert.Equal(b.RootEntityId, bIds.Root);
    }

    [Fact]
    public async Task GetNeighbours_ReachesFurtherAtDepthTwoOnBothBackends()
    {
        if (_neo4j is null) return;

        var aIds = await SeedChainAsync(_sqlite);
        var bIds = await SeedChainAsync(_neo4j);

        var a = await _sqlite.GetNeighboursAsync(aIds.Root, depth: 2);
        var b = await _neo4j.GetNeighboursAsync(bIds.Root, depth: 2);

        Assert.Equal(
            a.Entities.Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal),
            b.Entities.Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(b.Entities, e => e.Name == N("Leaf"));
    }

    [Fact]
    public async Task FindPath_AgreesOnHopCountAndEndpointsAcrossBothBackends()
    {
        if (_neo4j is null) return;

        var aIds = await SeedChainAsync(_sqlite);
        var bIds = await SeedChainAsync(_neo4j);

        var a = await _sqlite.FindPathAsync(aIds.Root, aIds.Leaf, maxHops: 5);
        var b = await _neo4j.FindPathAsync(bIds.Root, bIds.Leaf, maxHops: 5);

        Assert.False(a.IsEmpty);
        Assert.False(b.IsEmpty);
        Assert.Equal(a.HopCount, b.HopCount);
        Assert.Equal(N("Root"), b.Steps.First().Entity.Name);
        Assert.Equal(N("Leaf"), b.Steps.Last().Entity.Name);
    }

    [Fact]
    public async Task FindPath_ReturnsEmptyForDisconnectedEntitiesOnBothBackends()
    {
        if (_neo4j is null) return;

        var (aFrom, _) = await SeedPairAsync(_sqlite);
        var (bFrom, _) = await SeedPairAsync(_neo4j);
        var aIsland = await _sqlite.UpsertEntityAsync(N("Island"), "Concept", "general");
        var bIsland = await _neo4j.UpsertEntityAsync(N("Island"), "Concept", "general");

        Assert.True((await _sqlite.FindPathAsync(aFrom.Id, aIsland.Id, maxHops: 3)).IsEmpty);
        Assert.True((await _neo4j.FindPathAsync(bFrom.Id, bIsland.Id, maxHops: 3)).IsEmpty);
    }

    // ── the cross-store read (claims stay in SQLite by decision) ──

    [Fact]
    public async Task EntityConfidence_IsStillDerivedFromTheSqliteClaimsStoreOnBothBackends()
    {
        if (_neo4j is null) return;

        var a = await _sqlite.UpsertEntityAsync(N("Claimed"), "Concept", "general");
        var b = await _neo4j.UpsertEntityAsync(N("Claimed"), "Concept", "general");

        Assert.Equal(0.5f, a.Confidence, 4);
        Assert.Equal(0.5f, b.Confidence, 4);

        // One claim naming BOTH entity ids — the two backends allocate different ids for the same entity.
        await InsertClaimAsync(0.9, a.Id, b.Id);

        var a2 = await _sqlite.UpsertEntityAsync(N("Claimed"), "Concept", "general");
        var b2 = await _neo4j.UpsertEntityAsync(N("Claimed"), "Concept", "general");

        Assert.Equal(0.9f, a2.Confidence, 4);
        Assert.Equal(0.9f, b2.Confidence, 4);
        Assert.Equal(1, a2.SourceCount);
        Assert.Equal(1, b2.SourceCount);
    }

    [Fact]
    public async Task Neo4jGraph_WithNoClaimsStoreConfigured_KeepsTheDefaultConfidenceRatherThanCrashing()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        // A host that has not wired the claims store must degrade to the default, not throw — entity
        // confidence is the ConfidenceTracker's job, and the graph is only mirroring it.
        await using var isolated = new Neo4jKnowledgeGraph(
            Neo4jAvailability.Options with { ClaimsConnectionString = null },
            NullLogger<Neo4jKnowledgeGraph>.Instance);
        await isolated.InitializeAsync();

        var entity = await isolated.UpsertEntityAsync($"{Guid.NewGuid():N}_NoClaims", "Concept", "general");
        Assert.Equal(0.5f, entity.Confidence, 4);
    }

    // ── helpers ──

    private IEnumerable<IKnowledgeGraph> Both()
    {
        yield return _sqlite;
        yield return _neo4j!;
    }

    private async Task<(KgEntity From, KgEntity To)> SeedPairAsync(IKnowledgeGraph g)
    {
        var from = await g.UpsertEntityAsync(N("Sensor"), "Device", "prosthetics");
        var to = await g.UpsertEntityAsync(N("Muscle Signal"), "Concept", "prosthetics");
        return (from, to);
    }

    private async Task<(string Root, string Mid, string Leaf)> SeedChainAsync(IKnowledgeGraph g)
    {
        var root = await g.UpsertEntityAsync(N("Root"), "Concept", "general");
        var mid = await g.UpsertEntityAsync(N("Mid"), "Concept", "general");
        var leaf = await g.UpsertEntityAsync(N("Leaf"), "Concept", "general");
        await g.UpsertRelationAsync(root.Id, mid.Id, "leads_to");
        await g.UpsertRelationAsync(mid.Id, leaf.Id, "leads_to");
        return (root.Id, mid.Id, leaf.Id);
    }

    private async Task InsertClaimAsync(double confidence, params string[] entityIds)
    {
        await using var conn = new SqliteConnection(_sqliteConn);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO confidence_claims (id, statement, domain, entity_ids, confidence, created_at, updated_at)
            VALUES (@Id, @Statement, 'general', @EntityIds, @Confidence, @Now, @Now)
            """,
            new
            {
                Id = Guid.NewGuid().ToString("N"),
                Statement = "parity fixture",
                EntityIds = System.Text.Json.JsonSerializer.Serialize(entityIds),
                Confidence = confidence,
                Now = DateTime.UtcNow.ToString("O"),
            });
    }
}

/// <summary>
/// Serializes every class that talks to Neo4j. They share one live server and each wipes it between tests,
/// so letting xunit run them in parallel would have them deleting each other's fixtures mid-assertion.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Neo4jCollection
{
    public const string Name = "Neo4j";
}

/// <summary>
/// Probes once whether a live, DISPOSABLE Neo4j is available, so the parity suite can no-op on machines
/// without one. The probe result is cached: an unreachable server should cost one timeout per run, not one
/// per test.
///
/// <para>The suite requires <c>DARCI_NEO4J_TEST_WIPE=1</c> in addition to the connection settings, because
/// it empties the graph between tests (see <see cref="WipeAsync"/>). That opt-in is the safety interlock:
/// pointing the tests at a real graph by accident costs nothing, since without the flag they simply do not
/// run.</para>
/// </summary>
internal static class Neo4jAvailability
{
    private static readonly Lazy<(bool Available, string Reason)> Probe = new(() =>
    {
        var options = Neo4jOptions.FromEnvironment();
        if (!options.IsConfigured)
        {
            return (false, "DARCI_NEO4J_PASSWORD is not set — no Neo4j configured for this host.");
        }

        if (Environment.GetEnvironmentVariable("DARCI_NEO4J_TEST_WIPE") != "1")
        {
            return (false, "DARCI_NEO4J_TEST_WIPE is not 1 — refusing to wipe a graph that was not declared disposable.");
        }

        try
        {
            using var driver = Neo4j.Driver.GraphDatabase.Driver(
                options.Uri, Neo4j.Driver.AuthTokens.Basic(options.User, options.Password));
            driver.VerifyConnectivityAsync().Wait(TimeSpan.FromSeconds(10));
            return (true, "connected");
        }
        catch (Exception ex)
        {
            return (false, $"Neo4j unreachable at {options.Uri}: {ex.GetBaseException().Message}");
        }
    });

    public static Neo4jOptions Options { get; } = Neo4jOptions.FromEnvironment();

    public static bool IsAvailable => Probe.Value.Available;

    public static string Reason => Probe.Value.Reason;

    /// <summary>Empties the graph so each test starts from the same blank slate the SQLite side gets.</summary>
    public static async Task WipeAsync()
    {
        await using var driver = Neo4j.Driver.GraphDatabase.Driver(
            Options.Uri, Neo4j.Driver.AuthTokens.Basic(Options.User, Options.Password));
        await using var session = driver.AsyncSession(o => o.WithDatabase(Options.Database));
        await session.ExecuteWriteAsync(async tx => await RunWriteAsync(tx, "MATCH (e:Entity) DETACH DELETE e"));
    }
}
