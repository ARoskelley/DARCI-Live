#nullable enable

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>SQLite-backed gap store. Columnar so learning passes can query gaps by status/node/confidence.</summary>
public sealed class SqliteGapStore : IGapStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteGapStore> _logger;

    public SqliteGapStore(string connectionString, ILogger<SqliteGapStore> logger)
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
            CREATE TABLE IF NOT EXISTS node_gaps (
                id TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                origin_packet_id TEXT NOT NULL,
                origin_node INTEGER NOT NULL,
                question TEXT NOT NULL,
                intent TEXT NOT NULL,
                missing TEXT NOT NULL,
                confidence_score REAL NOT NULL,
                confidence_note TEXT NULL,
                status TEXT NOT NULL,
                goal_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_node_gaps_status ON node_gaps(status, updated_at);
            CREATE INDEX IF NOT EXISTS ix_node_gaps_correlation ON node_gaps(correlation_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Node gap store initialized.");
    }

    public async Task AddAsync(GapRecord gap, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO node_gaps
                (id, correlation_id, origin_packet_id, origin_node, question, intent, missing,
                 confidence_score, confidence_note, status, goal_id, created_at, updated_at)
            VALUES
                ($id, $corr, $pkt, $node, $q, $intent, $missing,
                 $cscore, $cnote, $status, $goal, $created, $updated)
            """;
        Bind(cmd, gap);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task UpdateAsync(GapRecord gap, CancellationToken ct = default) => AddAsync(gap, ct); // upsert

    public async Task<GapRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM node_gaps WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<GapRecord>> GetByStatusAsync(string status, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM node_gaps WHERE status = $status ORDER BY updated_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadAll(cmd, ct);
    }

    public async Task<IReadOnlyList<GapRecord>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM node_gaps WHERE correlation_id = $corr ORDER BY created_at";
        cmd.Parameters.AddWithValue("$corr", correlationId);
        return await ReadAll(cmd, ct);
    }

    private static void Bind(SqliteCommand cmd, GapRecord g)
    {
        cmd.Parameters.AddWithValue("$id", g.Id);
        cmd.Parameters.AddWithValue("$corr", g.CorrelationId);
        cmd.Parameters.AddWithValue("$pkt", g.OriginPacketId);
        cmd.Parameters.AddWithValue("$node", (int)g.OriginNode);
        cmd.Parameters.AddWithValue("$q", g.Question);
        cmd.Parameters.AddWithValue("$intent", g.Intent);
        cmd.Parameters.AddWithValue("$missing", g.Missing);
        cmd.Parameters.AddWithValue("$cscore", g.Confidence.Score);
        cmd.Parameters.AddWithValue("$cnote", (object?)g.Confidence.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", g.Status);
        cmd.Parameters.AddWithValue("$goal", (object?)g.GoalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", ToIso(g.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToIso(g.UpdatedAt));
    }

    private static async Task<IReadOnlyList<GapRecord>> ReadAll(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<GapRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Map(reader));
        return list;
    }

    private static GapRecord Map(SqliteDataReader r)
    {
        var noteOrd = r.GetOrdinal("confidence_note");
        var goalOrd = r.GetOrdinal("goal_id");
        return new GapRecord
        {
            Id = r.GetString(r.GetOrdinal("id")),
            CorrelationId = r.GetString(r.GetOrdinal("correlation_id")),
            OriginPacketId = r.GetString(r.GetOrdinal("origin_packet_id")),
            OriginNode = (NodeId)r.GetInt32(r.GetOrdinal("origin_node")),
            Question = r.GetString(r.GetOrdinal("question")),
            Intent = r.GetString(r.GetOrdinal("intent")),
            Missing = r.GetString(r.GetOrdinal("missing")),
            Confidence = new Confidence(
                r.GetDouble(r.GetOrdinal("confidence_score")),
                r.IsDBNull(noteOrd) ? null : r.GetString(noteOrd)),
            Status = r.GetString(r.GetOrdinal("status")),
            GoalId = r.IsDBNull(goalOrd) ? null : r.GetString(goalOrd),
            CreatedAt = FromIso(r.GetString(r.GetOrdinal("created_at"))),
            UpdatedAt = FromIso(r.GetString(r.GetOrdinal("updated_at"))),
        };
    }

    private static string ToIso(DateTime v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromIso(string v) => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
