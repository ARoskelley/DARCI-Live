#nullable enable

using Darci.Memory.Confidence;
using Darci.Memory.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using static Darci.Memory.Graph.Neo4jWrite;

namespace Darci.Research.Agents.Tests.Contract;

/// <summary>
/// P2d.2 — the migration itself. A migrator that reports success without being able to report failure is
/// just an expensive no-op, so these cover both directions: a faithful copy verifies clean, AND a corrupted
/// copy is actually caught.
/// </summary>
[Collection(Neo4jCollection.Name)]
public sealed class KnowledgeGraphMigratorTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _sqliteConn;
    private KnowledgeGraph _sqlite = null!;

    public KnowledgeGraphMigratorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-migrate-{Guid.NewGuid():N}.db");
        _sqliteConn = $"Data Source={_dbPath}";
    }

    public async Task InitializeAsync()
    {
        _sqlite = new KnowledgeGraph(_sqliteConn, NullLogger<KnowledgeGraph>.Instance);
        await _sqlite.InitializeAsync();
        await new ConfidenceTracker(_sqliteConn, _sqlite, NullLogger<ConfidenceTracker>.Instance)
            .InitializeAsync();

        if (!Neo4jAvailability.IsAvailable) return;
        await Neo4jAvailability.WipeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private KnowledgeGraphMigrator Migrator() => new(
        _sqliteConn,
        Neo4jAvailability.Options,
        NullLogger<KnowledgeGraphMigrator>.Instance);

    [Fact]
    public async Task Migrate_CopiesEveryEntityAndRelationAndVerifiesClean()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        var sensor = await _sqlite.UpsertEntityAsync(
            "EMG Sensor", "Device", "prosthetics", "Reads muscle signals.",
            new[] { "electromyography sensor" }, new[] { 0.1f, 0.2f, 0.3f });
        var signal = await _sqlite.UpsertEntityAsync("Muscle Signal", "Concept", "prosthetics");
        await _sqlite.UpsertRelationAsync(sensor.Id, signal.Id, "measures", 0.8f, 0.7f, new[] { "ev1" });

        var report = await Migrator().MigrateAsync();

        Assert.True(report.IsClean, string.Join(" | ", report.Mismatches));
        Assert.Equal(2, report.SourceEntities);
        Assert.Equal(2, report.TargetEntities);
        Assert.Equal(1, report.SourceRelations);
        Assert.Equal(1, report.TargetRelations);
    }

    [Fact]
    public async Task Migrate_PreservesEntityIdsSoClaimsStillResolve()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        // Entity confidence is derived by matching entity ids inside confidence_claims, which stays in
        // SQLite. Regenerating ids during migration would sever every entity from its evidence silently.
        var entity = await _sqlite.UpsertEntityAsync("Torque Sensor", "Device", "mechanics");
        await Migrator().MigrateAsync();

        await using var neo4j = new Neo4jKnowledgeGraph(
            Neo4jAvailability.Options with { ClaimsConnectionString = _sqliteConn },
            NullLogger<Neo4jKnowledgeGraph>.Instance);

        var migrated = await neo4j.GetEntityAsync(entity.Id);
        Assert.NotNull(migrated);
        Assert.Equal(entity.Name, migrated!.Name);
    }

    [Fact]
    public async Task Migrate_IsIdempotentAndDoesNotDuplicate()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        var a = await _sqlite.UpsertEntityAsync("Alpha", "Concept", "general");
        var b = await _sqlite.UpsertEntityAsync("Beta", "Concept", "general");
        await _sqlite.UpsertRelationAsync(a.Id, b.Id, "leads_to");

        await Migrator().MigrateAsync();
        var second = await Migrator().MigrateAsync();

        Assert.True(second.IsClean, string.Join(" | ", second.Mismatches));
        Assert.Equal(2, second.TargetEntities);
        Assert.Equal(1, second.TargetRelations);
    }

    [Fact]
    public async Task Verify_DetectsAMissingEntity()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        var entity = await _sqlite.UpsertEntityAsync("Will Vanish", "Concept", "general");
        await Migrator().MigrateAsync();

        await DeleteFromNeo4jAsync(entity.Id);

        var report = await Migrator().VerifyAsync();
        Assert.False(report.IsClean);
        Assert.Contains(report.Mismatches, m => m.Contains(entity.Id) && m.Contains("missing"));
    }

    [Fact]
    public async Task Verify_DetectsATamperedField()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        var entity = await _sqlite.UpsertEntityAsync("Original Name", "Concept", "general");
        await Migrator().MigrateAsync();

        await SetNameInNeo4jAsync(entity.Id, "Tampered Name");

        var report = await Migrator().VerifyAsync();
        Assert.False(report.IsClean);
        Assert.Contains(report.Mismatches, m => m.Contains("name") && m.Contains("Tampered Name"));
    }

    [Fact]
    public async Task Verify_DetectsAnEntityThatExistsOnlyInNeo4j()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        await _sqlite.UpsertEntityAsync("Legitimate", "Concept", "general");
        await Migrator().MigrateAsync();

        // A leftover fixture or stale import — reads would surface an entity the system of record never had.
        var orphanId = Guid.NewGuid().ToString("N");
        await CreateOrphanInNeo4jAsync(orphanId, "Orphan");

        var report = await Migrator().VerifyAsync();
        Assert.False(report.IsClean);
        Assert.Contains(report.Mismatches, m => m.Contains(orphanId) && m.Contains("not in SQLite"));
    }

    [Fact]
    public async Task Verify_OnAnEmptyGraph_ReportsClean()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        var report = await Migrator().VerifyAsync();
        Assert.True(report.IsClean);
        Assert.Equal(0, report.SourceEntities);
    }

    private static async Task DeleteFromNeo4jAsync(string id)
    {
        await using var driver = GraphDatabase.Driver(
            Neo4jAvailability.Options.Uri,
            AuthTokens.Basic(Neo4jAvailability.Options.User, Neo4jAvailability.Options.Password));
        await using var session = driver.AsyncSession(o => o.WithDatabase(Neo4jAvailability.Options.Database));
        await session.ExecuteWriteAsync(async tx =>
            await RunWriteAsync(tx, "MATCH (e:Entity {id: $id}) DETACH DELETE e", new { id }));
    }

    private static async Task CreateOrphanInNeo4jAsync(string id, string name)
    {
        await using var driver = GraphDatabase.Driver(
            Neo4jAvailability.Options.Uri,
            AuthTokens.Basic(Neo4jAvailability.Options.User, Neo4jAvailability.Options.Password));
        await using var session = driver.AsyncSession(o => o.WithDatabase(Neo4jAvailability.Options.Database));
        await session.ExecuteWriteAsync(async tx =>
            await RunWriteAsync(tx, "CREATE (e:Entity {id: $id, name: $name})", new { id, name }));
    }

    private static async Task SetNameInNeo4jAsync(string id, string name)
    {
        await using var driver = GraphDatabase.Driver(
            Neo4jAvailability.Options.Uri,
            AuthTokens.Basic(Neo4jAvailability.Options.User, Neo4jAvailability.Options.Password));
        await using var session = driver.AsyncSession(o => o.WithDatabase(Neo4jAvailability.Options.Database));
        await session.ExecuteWriteAsync(async tx =>
            await RunWriteAsync(tx, "MATCH (e:Entity {id: $id}) SET e.name = $name", new { id, name }));
    }
}
