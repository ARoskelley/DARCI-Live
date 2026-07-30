#nullable enable

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// SQLite-backed packet store. Two tables: <c>node_packets</c> (header, one row per packet) and
/// <c>node_log</c> (append-only entries, ordered by seq). The schema is deliberately columnar — state,
/// confidence, success, node, and timestamps are first-class columns so later learning passes can
/// query "which decisions by which node at what confidence led to success/failure" without parsing
/// blobs (decision 5). The structured payload travels as JSON in one column (decision 4).
/// </summary>
public sealed class SqliteNodePacketStore : INodePacketStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteNodePacketStore> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public SqliteNodePacketStore(string connectionString, ILogger<SqliteNodePacketStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS node_packets (
                id TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                address INTEGER NULL,
                requested_capability INTEGER NULL,
                state INTEGER NOT NULL,
                intent TEXT NOT NULL,
                success_criteria TEXT NULL,
                payload_slots_json TEXT NOT NULL,
                log_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                lease_expires_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_node_packets_correlation ON node_packets(correlation_id);
            CREATE INDEX IF NOT EXISTS ix_node_packets_state ON node_packets(state, updated_at);
            CREATE INDEX IF NOT EXISTS ix_node_packets_lease ON node_packets(state, lease_expires_at);

            CREATE TABLE IF NOT EXISTS node_log (
                packet_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                node INTEGER NOT NULL,
                at TEXT NOT NULL,
                state_after INTEGER NOT NULL,
                decision TEXT NOT NULL,
                confidence_score REAL NOT NULL,
                confidence_note TEXT NULL,
                success INTEGER NULL,
                error TEXT NULL,
                artifacts_json TEXT NOT NULL,
                PRIMARY KEY (packet_id, seq)
            );

            CREATE INDEX IF NOT EXISTS ix_node_log_packet ON node_log(packet_id, seq);
            CREATE INDEX IF NOT EXISTS ix_node_log_node_success ON node_log(node, success);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // SU5 — additive migration to canonical STRING keys. The ordinal columns stay (nothing is dropped and
        // no read path changes yet); these carry the values an external node's capability could never fit in
        // an enum ordinal, and become the source of truth when the domain types switch in SU6.
        await SqliteEnumKeyMigration.EnsureColumnAsync(conn, "node_packets", "address_key", "TEXT NULL", ct);
        await SqliteEnumKeyMigration.EnsureColumnAsync(conn, "node_packets", "capability_key", "TEXT NULL", ct);
        await SqliteEnumKeyMigration.EnsureColumnAsync(conn, "node_log", "node_key", "TEXT NULL", ct);

        var backfilled =
            await SqliteEnumKeyMigration.BackfillNodeKeysAsync(conn, "node_packets", "address", "address_key", ct) +
            await SqliteEnumKeyMigration.BackfillCapabilityKeysAsync(conn, "node_packets", "requested_capability", "capability_key", ct) +
            await SqliteEnumKeyMigration.BackfillNodeKeysAsync(conn, "node_log", "node", "node_key", ct);

        await using (var index = conn.CreateCommand())
        {
            index.CommandText = "CREATE INDEX IF NOT EXISTS ix_node_packets_capability_key ON node_packets(capability_key);";
            await index.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Node packet store initialized{Backfill}.",
            backfilled > 0 ? $" (backfilled {backfilled} string key(s))" : "");
    }

    public async Task CreatePacketAsync(NodePacket packet, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await UpsertHeaderAsync(conn, tx, packet, ct);
        for (var i = 0; i < packet.Log.Count; i++)
            await InsertLogEntryAsync(conn, tx, packet.Id, i, packet.Log[i], ct);

        await tx.CommitAsync(ct);
    }

    public async Task SavePacketAsync(NodePacket packet, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // How many log rows are already persisted for this packet?
        int persisted;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM node_log WHERE packet_id = $id";
            countCmd.Parameters.AddWithValue("$id", packet.Id);
            persisted = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        await UpsertHeaderAsync(conn, tx, packet, ct);

        // Append only entries beyond what's already stored. (Append-only: never rewrite history.)
        for (var i = persisted; i < packet.Log.Count; i++)
            await InsertLogEntryAsync(conn, tx, packet.Id, i, packet.Log[i], ct);

        await tx.CommitAsync(ct);
    }

    private static async Task UpsertHeaderAsync(SqliteConnection conn, SqliteTransaction tx, NodePacket p, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO node_packets
                (id, correlation_id, address, requested_capability, address_key, capability_key, state, intent, success_criteria,
                 payload_slots_json, log_count, created_at, updated_at, lease_expires_at)
            VALUES
                ($id, $corr, $addr, $cap, $addr_key, $cap_key, $state, $intent, $success_criteria,
                 $slots, $log_count, $created_at, $updated_at, $lease)
            ON CONFLICT(id) DO UPDATE SET
                correlation_id = excluded.correlation_id,
                address = excluded.address,
                requested_capability = excluded.requested_capability,
                address_key = excluded.address_key,
                capability_key = excluded.capability_key,
                state = excluded.state,
                intent = excluded.intent,
                success_criteria = excluded.success_criteria,
                payload_slots_json = excluded.payload_slots_json,
                log_count = excluded.log_count,
                updated_at = excluded.updated_at,
                lease_expires_at = excluded.lease_expires_at
            """;
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$corr", p.CorrelationId);
        cmd.Parameters.AddWithValue("$addr", p.Address is null ? DBNull.Value : (int)p.Address.Value);
        cmd.Parameters.AddWithValue("$cap", p.RequestedCapability is null ? DBNull.Value : (int)p.RequestedCapability.Value);
        // Dual-write the canonical string keys (SU5). Reads still use the ordinals until SU6.
        cmd.Parameters.AddWithValue("$addr_key", p.Address is null ? DBNull.Value : CapabilityKey.From(p.Address.Value));
        cmd.Parameters.AddWithValue("$cap_key", p.RequestedCapability is null ? DBNull.Value : CapabilityKey.From(p.RequestedCapability.Value));
        cmd.Parameters.AddWithValue("$state", (int)p.State);
        cmd.Parameters.AddWithValue("$intent", p.Payload.Intent);
        cmd.Parameters.AddWithValue("$success_criteria", (object?)p.Payload.SuccessCriteria ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$slots", JsonSerializer.Serialize(p.Payload.Slots, JsonOpts));
        cmd.Parameters.AddWithValue("$log_count", p.Log.Count);
        cmd.Parameters.AddWithValue("$created_at", ToIso(p.CreatedAt));
        cmd.Parameters.AddWithValue("$updated_at", ToIso(p.UpdatedAt));
        cmd.Parameters.AddWithValue("$lease", p.LeaseExpiresAt is null ? DBNull.Value : ToIso(p.LeaseExpiresAt.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertLogEntryAsync(SqliteConnection conn, SqliteTransaction tx, string packetId, int seq, NodeLogEntry e, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO node_log
                (packet_id, seq, node, node_key, at, state_after, decision, confidence_score, confidence_note,
                 success, error, artifacts_json)
            VALUES
                ($pid, $seq, $node, $node_key, $at, $state_after, $decision, $cscore, $cnote, $success, $error, $artifacts)
            """;
        cmd.Parameters.AddWithValue("$pid", packetId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$node", (int)e.Node);
        cmd.Parameters.AddWithValue("$node_key", CapabilityKey.From(e.Node));
        cmd.Parameters.AddWithValue("$at", ToIso(e.At));
        cmd.Parameters.AddWithValue("$state_after", (int)e.StateAfter);
        cmd.Parameters.AddWithValue("$decision", e.Decision);
        cmd.Parameters.AddWithValue("$cscore", e.Confidence.Score);
        cmd.Parameters.AddWithValue("$cnote", (object?)e.Confidence.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$success", e.Success is null ? DBNull.Value : (e.Success.Value ? 1 : 0));
        cmd.Parameters.AddWithValue("$error", (object?)e.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artifacts", JsonSerializer.Serialize(e.Artifacts, JsonOpts));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<NodePacket?> GetPacketAsync(string packetId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        NodePacket? header;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM node_packets WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", packetId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            header = MapHeader(reader);
        }

        var log = await ReadLogAsync(conn, packetId, ct);
        return header! with { Log = log };
    }

    public async Task<NodePacketStatus?> GetStatusAsync(string packetId, CancellationToken ct = default)
    {
        var packet = await GetPacketAsync(packetId, ct);
        return packet?.ToStatus();
    }

    public async Task<IReadOnlyList<NodePacket>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var ids = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM node_packets WHERE correlation_id = $corr ORDER BY created_at";
            cmd.Parameters.AddWithValue("$corr", correlationId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }

        var results = new List<NodePacket>(ids.Count);
        foreach (var id in ids)
        {
            var p = await GetPacketAsync(id, ct);
            if (p is not null) results.Add(p);
        }
        return results;
    }

    public async Task<IReadOnlyList<NodePacket>> GetByStatesAsync(IReadOnlyList<NodeState> states, int limit = 100, CancellationToken ct = default)
    {
        if (states.Count == 0) return Array.Empty<NodePacket>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var placeholders = string.Join(",", states.Select((_, i) => $"$s{i}"));
        var ids = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT id FROM node_packets WHERE state IN ({placeholders}) ORDER BY updated_at DESC LIMIT $limit";
            for (var i = 0; i < states.Count; i++)
                cmd.Parameters.AddWithValue($"$s{i}", (int)states[i]);
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }

        var results = new List<NodePacket>(ids.Count);
        foreach (var id in ids)
        {
            var p = await GetPacketAsync(id, ct);
            if (p is not null) results.Add(p);
        }
        return results;
    }

    public Task<IReadOnlyList<NodePacket>> GetActivePacketsWithExpiredLeaseAsync(DateTime nowUtc, CancellationToken ct = default)
        => QueryActiveAsync(nowUtc, requireExpiredLease: true, ct);

    public Task<IReadOnlyList<NodePacket>> GetActivePacketsAsync(CancellationToken ct = default)
        => QueryActiveAsync(DateTime.UtcNow, requireExpiredLease: false, ct);

    private async Task<IReadOnlyList<NodePacket>> QueryActiveAsync(DateTime nowUtc, bool requireExpiredLease, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Active = state < Succeeded (Created..AwaitingDependency are 0..4; terminal are 5..7).
        var ids = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = requireExpiredLease
                ? "SELECT id FROM node_packets WHERE state <= $maxActive AND lease_expires_at IS NOT NULL AND lease_expires_at < $now ORDER BY updated_at"
                : "SELECT id FROM node_packets WHERE state <= $maxActive ORDER BY updated_at";
            cmd.Parameters.AddWithValue("$maxActive", (int)NodeState.AwaitingDependency);
            if (requireExpiredLease) cmd.Parameters.AddWithValue("$now", ToIso(nowUtc));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        }

        var results = new List<NodePacket>(ids.Count);
        foreach (var id in ids)
        {
            var p = await GetPacketAsync(id, ct);
            if (p is not null) results.Add(p);
        }
        return results;
    }

    private static async Task<List<NodeLogEntry>> ReadLogAsync(SqliteConnection conn, string packetId, CancellationToken ct)
    {
        var log = new List<NodeLogEntry>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM node_log WHERE packet_id = $id ORDER BY seq";
        cmd.Parameters.AddWithValue("$id", packetId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var successOrd = reader.GetOrdinal("success");
            var noteOrd = reader.GetOrdinal("confidence_note");
            var errOrd = reader.GetOrdinal("error");
            log.Add(new NodeLogEntry
            {
                Node = (NodeId)reader.GetInt32(reader.GetOrdinal("node")),
                At = FromIso(reader.GetString(reader.GetOrdinal("at"))),
                StateAfter = (NodeState)reader.GetInt32(reader.GetOrdinal("state_after")),
                Decision = reader.GetString(reader.GetOrdinal("decision")),
                Confidence = new Confidence(
                    reader.GetDouble(reader.GetOrdinal("confidence_score")),
                    reader.IsDBNull(noteOrd) ? null : reader.GetString(noteOrd)),
                Success = reader.IsDBNull(successOrd) ? null : reader.GetInt32(successOrd) == 1,
                Error = reader.IsDBNull(errOrd) ? null : reader.GetString(errOrd),
                Artifacts = DeserializeArtifacts(reader.GetString(reader.GetOrdinal("artifacts_json"))),
            });
        }
        return log;
    }

    private static NodePacket MapHeader(SqliteDataReader reader)
    {
        var addrOrd = reader.GetOrdinal("address");
        var capOrd = reader.GetOrdinal("requested_capability");
        var scOrd = reader.GetOrdinal("success_criteria");
        var leaseOrd = reader.GetOrdinal("lease_expires_at");

        var slots = DeserializeSlots(reader.GetString(reader.GetOrdinal("payload_slots_json")));

        return new NodePacket
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            CorrelationId = reader.GetString(reader.GetOrdinal("correlation_id")),
            Address = reader.IsDBNull(addrOrd) ? null : (NodeId)reader.GetInt32(addrOrd),
            RequestedCapability = reader.IsDBNull(capOrd) ? null : (Capability)reader.GetInt32(capOrd),
            State = (NodeState)reader.GetInt32(reader.GetOrdinal("state")),
            Payload = new PacketPayload
            {
                Intent = reader.GetString(reader.GetOrdinal("intent")),
                SuccessCriteria = reader.IsDBNull(scOrd) ? null : reader.GetString(scOrd),
                Slots = slots,
            },
            CreatedAt = FromIso(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = FromIso(reader.GetString(reader.GetOrdinal("updated_at"))),
            LeaseExpiresAt = reader.IsDBNull(leaseOrd) ? null : FromIso(reader.GetString(leaseOrd)),
        };
    }

    private static Dictionary<string, string> DeserializeSlots(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static List<string> DeserializeArtifacts(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    private static string ToIso(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime FromIso(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
