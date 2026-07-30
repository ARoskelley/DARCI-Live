using Darci.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// SU5 — the additive enum-ordinal → string-key migration. The risk here is data: a real database with real
/// history. These tests prove the migration is idempotent, backfills pre-existing rows, dual-writes new ones,
/// drops nothing, and leaves every existing read path working.
/// </summary>
public sealed class EnumKeyMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _conn;

    public EnumKeyMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-migrate-{Guid.NewGuid():N}.db");
        _conn = $"Data Source={_dbPath}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private SqliteNodePacketStore PacketStore() => new(_conn, NullLogger<SqliteNodePacketStore>.Instance);
    private SqliteGapStore GapStore() => new(_conn, NullLogger<SqliteGapStore>.Instance);

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var conn = new SqliteConnection(_conn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : Convert.ToString(v);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqliteConnection(_conn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ── the generated CASE SQL cannot drift from the in-code mapping ──

    [Fact]
    public void CapabilityCaseSql_CoversEveryEnumMember()
    {
        var sql = SqliteEnumKeyMigration.CapabilityCaseSql("requested_capability");
        foreach (Capability c in Enum.GetValues<Capability>())
        {
            Assert.Contains($"WHEN {(int)c} THEN '{CapabilityKey.From(c)}'", sql);
        }
    }

    [Fact]
    public void NodeCaseSql_CoversEveryEnumMember()
    {
        var sql = SqliteEnumKeyMigration.NodeCaseSql("origin_node");
        foreach (NodeId n in Enum.GetValues<NodeId>())
        {
            Assert.Contains($"WHEN {(int)n} THEN '{CapabilityKey.From(n)}'", sql);
        }
    }

    // ── schema migration ──

    [Fact]
    public async Task Initialize_AddsTheKeyColumns_AndIsIdempotent()
    {
        await PacketStore().InitializeAsync();
        await PacketStore().InitializeAsync();   // second run must not throw (no ADD COLUMN IF NOT EXISTS in SQLite)
        await PacketStore().InitializeAsync();

        await using var conn = new SqliteConnection(_conn);
        await conn.OpenAsync();
        Assert.True(await SqliteEnumKeyMigration.ColumnExistsAsync(conn, "node_packets", "address_key"));
        Assert.True(await SqliteEnumKeyMigration.ColumnExistsAsync(conn, "node_packets", "capability_key"));
        Assert.True(await SqliteEnumKeyMigration.ColumnExistsAsync(conn, "node_log", "node_key"));

        // Nothing was dropped — the ordinal columns are still there, so an older build can still read this DB.
        Assert.True(await SqliteEnumKeyMigration.ColumnExistsAsync(conn, "node_packets", "address"));
        Assert.True(await SqliteEnumKeyMigration.ColumnExistsAsync(conn, "node_packets", "requested_capability"));
        Assert.True(await SqliteEnumKeyMigration.ColumnExistsAsync(conn, "node_log", "node"));
    }

    [Fact]
    public async Task NewRows_DualWriteBothOrdinalAndStringKey()
    {
        var store = PacketStore();
        await store.InitializeAsync();

        var packet = NodePacket.Create("do it", address: NodeId.Coding, capability: Capability.WriteCode)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");
        await store.CreatePacketAsync(packet);

        Assert.Equal(((int)NodeId.Coding).ToString(), await ScalarAsync($"SELECT address FROM node_packets WHERE id='{packet.Id}'"));
        Assert.Equal(NodeKeys.Coding, await ScalarAsync($"SELECT address_key FROM node_packets WHERE id='{packet.Id}'"));
        Assert.Equal(Capabilities.CodingWrite, await ScalarAsync($"SELECT capability_key FROM node_packets WHERE id='{packet.Id}'"));
        Assert.Equal(NodeKeys.Orchestrator, await ScalarAsync($"SELECT node_key FROM node_log WHERE packet_id='{packet.Id}' AND seq=0"));
    }

    [Fact]
    public async Task NullAddressAndCapability_StayNullInBothForms()
    {
        var store = PacketStore();
        await store.InitializeAsync();

        var packet = NodePacket.Create("no address, no capability")
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");
        await store.CreatePacketAsync(packet);

        Assert.Null(await ScalarAsync($"SELECT address_key FROM node_packets WHERE id='{packet.Id}'"));
        Assert.Null(await ScalarAsync($"SELECT capability_key FROM node_packets WHERE id='{packet.Id}'"));
    }

    // ── the real risk: PRE-EXISTING rows written before the migration ──

    [Fact]
    public async Task PreMigrationRows_AreBackfilled_AndStillReadCorrectly()
    {
        // Build the OLD schema by hand (no *_key columns), insert a row the old way, then migrate.
        await ExecAsync("""
            CREATE TABLE node_packets (
                id TEXT PRIMARY KEY, correlation_id TEXT NOT NULL, address INTEGER NULL,
                requested_capability INTEGER NULL, state INTEGER NOT NULL, intent TEXT NOT NULL,
                success_criteria TEXT NULL, payload_slots_json TEXT NOT NULL, log_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, lease_expires_at TEXT NULL);
            CREATE TABLE node_log (
                packet_id TEXT NOT NULL, seq INTEGER NOT NULL, node INTEGER NOT NULL, at TEXT NOT NULL,
                state_after INTEGER NOT NULL, decision TEXT NOT NULL, confidence_score REAL NOT NULL,
                confidence_note TEXT NULL, success INTEGER NULL, error TEXT NULL, artifacts_json TEXT NOT NULL,
                PRIMARY KEY (packet_id, seq));
            """);
        await ExecAsync($$"""
            INSERT INTO node_packets (id, correlation_id, address, requested_capability, state, intent,
                payload_slots_json, log_count, created_at, updated_at)
            VALUES ('old-1', 'corr-old', {{(int)NodeId.Innovation}}, {{(int)Capability.Innovate}}, {{(int)NodeState.Succeeded}},
                'legacy work', '{}', 1, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO node_log (packet_id, seq, node, at, state_after, decision, confidence_score, artifacts_json)
            VALUES ('old-1', 0, {{(int)NodeId.Knowledge}}, '2026-01-01T00:00:00.0000000Z', {{(int)NodeState.Succeeded}}, 'legacy', -1, '[]');
            """);

        var store = PacketStore();
        await store.InitializeAsync();   // ← migration + backfill

        Assert.Equal(NodeKeys.Innovation, await ScalarAsync("SELECT address_key FROM node_packets WHERE id='old-1'"));
        Assert.Equal(Capabilities.InnovationSynthesize, await ScalarAsync("SELECT capability_key FROM node_packets WHERE id='old-1'"));
        Assert.Equal(NodeKeys.Knowledge, await ScalarAsync("SELECT node_key FROM node_log WHERE packet_id='old-1' AND seq=0"));

        // And the row still loads through the normal read path, unchanged.
        var loaded = await store.GetPacketAsync("old-1");
        Assert.NotNull(loaded);
        Assert.Equal(NodeId.Innovation, loaded!.Address);
        Assert.Equal(Capability.Innovate, loaded.RequestedCapability);
        Assert.Equal(NodeState.Succeeded, loaded.State);
        Assert.Equal(NodeId.Knowledge, loaded.Log[0].Node);
    }

    [Fact]
    public async Task Backfill_DoesNotOverwriteAnAlreadyPopulatedKey()
    {
        var store = PacketStore();
        await store.InitializeAsync();
        var packet = NodePacket.Create("x", capability: Capability.WriteCode)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");
        await store.CreatePacketAsync(packet);

        // Simulate a string-only capability with no enum equivalent — exactly what SU6 will start writing.
        await ExecAsync($"UPDATE node_packets SET capability_key='acme.simulate_thermal' WHERE id='{packet.Id}'");
        await PacketStore().InitializeAsync();   // re-run the migration

        Assert.Equal("acme.simulate_thermal", await ScalarAsync($"SELECT capability_key FROM node_packets WHERE id='{packet.Id}'"));
    }

    [Fact]
    public async Task GapStore_MigratesAndDualWrites()
    {
        var store = GapStore();
        await store.InitializeAsync();
        await store.AddAsync(new GapRecord
        {
            CorrelationId = "corr-1", OriginPacketId = "pkt-1", OriginNode = NodeId.Knowledge,
            Question = "q", Intent = "i", Missing = "m",
        });

        Assert.Equal(NodeKeys.Knowledge, await ScalarAsync("SELECT origin_node_key FROM node_gaps LIMIT 1"));
        await GapStore().InitializeAsync();   // idempotent
        Assert.Equal(NodeKeys.Knowledge, await ScalarAsync("SELECT origin_node_key FROM node_gaps LIMIT 1"));
    }

    // ── campaign protocol JSON: writes strings, still reads legacy numbers ──

    [Fact]
    public async Task CampaignProtocol_WritesStringEnums_AndStillReadsLegacyNumericJson()
    {
        var store = new SqliteValidationCampaignStore(_conn, NullLogger<SqliteValidationCampaignStore>.Instance);
        await store.InitializeAsync();

        var campaign = new ValidationCampaign
        {
            EntryId = "e1", HypothesisSnapshot = "h", CorrelationId = "corr-1",
            Protocol = new[]
            {
                new ValidationStep("s1", ValidationStepKind.SandboxTest, Capability.RunTests, NodeId.Coding,
                    new SuccessCriteria("pass_rate", Comparator.GreaterOrEqual, 0.9)),
            },
        };
        await store.AddAsync(campaign);

        // New rows persist readable strings, not ordinals.
        var json = await ScalarAsync($"SELECT protocol_json FROM validation_campaigns WHERE id='{campaign.Id}'");
        Assert.Contains("SandboxTest", json);
        Assert.Contains("RunTests", json);
        Assert.Contains("Coding", json);

        var roundTripped = await store.GetAsync(campaign.Id);
        Assert.Equal(Capability.RunTests, roundTripped!.Protocol[0].Capability);
        Assert.Equal(NodeId.Coding, roundTripped.Protocol[0].Environment);

        // A row written the OLD way must still load. Generate it exactly as the pre-SU5 code did: default
        // JsonSerializerOptions, which writes enums as bare ordinals.
        var legacyProtocol = new[]
        {
            new ValidationStep("s9", ValidationStepKind.ExternalResearchCheck, Capability.AnswerKnowledge, NodeId.Knowledge,
                new SuccessCriteria("corroborations", Comparator.GreaterOrEqual, 2), "legacy"),
        };
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(legacyProtocol, new System.Text.Json.JsonSerializerOptions());
        Assert.Contains($"\"Kind\":{(int)ValidationStepKind.ExternalResearchCheck}", legacyJson);   // truly numeric
        await ExecAsync($"UPDATE validation_campaigns SET protocol_json='{legacyJson.Replace("'", "''")}' WHERE id='{campaign.Id}'");

        var legacyLoaded = await store.GetAsync(campaign.Id);
        Assert.Equal("s9", legacyLoaded!.Protocol[0].Id);
        Assert.Equal(ValidationStepKind.ExternalResearchCheck, legacyLoaded.Protocol[0].Kind);
        Assert.Equal(Capability.AnswerKnowledge, legacyLoaded.Protocol[0].Capability);
        Assert.Equal(NodeId.Knowledge, legacyLoaded.Protocol[0].Environment);
    }
}
