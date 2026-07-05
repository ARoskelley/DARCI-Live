#nullable enable

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>SQLite-backed store of human-decision proposals.</summary>
public sealed class SqliteProposalStore : IProposalStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteProposalStore> _logger;

    public SqliteProposalStore(string connectionString, ILogger<SqliteProposalStore> logger)
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
            CREATE TABLE IF NOT EXISTS human_proposals (
                id TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                subject_id TEXT NOT NULL,
                target_provenance INTEGER NOT NULL,
                title TEXT NOT NULL,
                summary TEXT NOT NULL,
                justification_json TEXT NOT NULL,
                status INTEGER NOT NULL,
                parked_packet_id TEXT NULL,
                decision_note TEXT NULL,
                decided_by TEXT NULL,
                decided_at TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_human_proposals_status ON human_proposals(status, created_at);
            CREATE INDEX IF NOT EXISTS ix_human_proposals_parked ON human_proposals(parked_packet_id, status);
            CREATE INDEX IF NOT EXISTS ix_human_proposals_correlation ON human_proposals(correlation_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Human proposal store initialized.");
    }

    public async Task AddAsync(HumanProposal p, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO human_proposals
                (id, correlation_id, kind, subject_id, target_provenance, title, summary, justification_json,
                 status, parked_packet_id, decision_note, decided_by, decided_at, created_at, updated_at)
            VALUES
                ($id, $corr, $kind, $subj, $tp, $title, $summary, $just,
                 $status, $parked, $note, $by, $decided, $created, $updated)
            """;
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$corr", p.CorrelationId);
        cmd.Parameters.AddWithValue("$kind", (int)p.Kind);
        cmd.Parameters.AddWithValue("$subj", p.SubjectId);
        cmd.Parameters.AddWithValue("$tp", (int)p.TargetProvenance);
        cmd.Parameters.AddWithValue("$title", p.Title);
        cmd.Parameters.AddWithValue("$summary", p.Summary);
        cmd.Parameters.AddWithValue("$just", p.JustificationJson);
        cmd.Parameters.AddWithValue("$status", (int)p.Status);
        cmd.Parameters.AddWithValue("$parked", (object?)p.ParkedPacketId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)p.DecisionNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$by", (object?)p.DecidedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$decided", p.DecidedAt is null ? DBNull.Value : ToIso(p.DecidedAt.Value));
        cmd.Parameters.AddWithValue("$created", ToIso(p.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToIso(p.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<HumanProposal?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM human_proposals WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<HumanProposal>> GetPendingAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM human_proposals WHERE status = 0 ORDER BY created_at LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadAll(cmd, ct);
    }

    public async Task<IReadOnlyList<HumanProposal>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM human_proposals WHERE correlation_id = $corr ORDER BY created_at";
        cmd.Parameters.AddWithValue("$corr", correlationId);
        return await ReadAll(cmd, ct);
    }

    public async Task<bool> RecordDecisionAsync(string id, HumanProposalStatus status, string? decidedBy, string? note, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Only decide a still-Pending proposal (idempotent / no double-decide).
        cmd.CommandText = """
            UPDATE human_proposals SET status = $status, decided_by = $by, decision_note = $note, decided_at = $at, updated_at = $at
            WHERE id = $id AND status = 0
            """;
        cmd.Parameters.AddWithValue("$status", (int)status);
        cmd.Parameters.AddWithValue("$by", (object?)decidedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", ToIso(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> HasPendingForParkedPacketAsync(string parkedPacketId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM human_proposals WHERE parked_packet_id = $id AND status = 0";
        cmd.Parameters.AddWithValue("$id", parkedPacketId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<IReadOnlyList<HumanProposal>> ReadAll(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<HumanProposal>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Map(reader));
        return list;
    }

    private static HumanProposal Map(SqliteDataReader r)
    {
        var parkedOrd = r.GetOrdinal("parked_packet_id");
        var noteOrd = r.GetOrdinal("decision_note");
        var byOrd = r.GetOrdinal("decided_by");
        var decidedOrd = r.GetOrdinal("decided_at");
        return new HumanProposal
        {
            Id = r.GetString(r.GetOrdinal("id")),
            CorrelationId = r.GetString(r.GetOrdinal("correlation_id")),
            Kind = (HumanProposalKind)r.GetInt32(r.GetOrdinal("kind")),
            SubjectId = r.GetString(r.GetOrdinal("subject_id")),
            TargetProvenance = (Provenance)r.GetInt32(r.GetOrdinal("target_provenance")),
            Title = r.GetString(r.GetOrdinal("title")),
            Summary = r.GetString(r.GetOrdinal("summary")),
            JustificationJson = r.GetString(r.GetOrdinal("justification_json")),
            Status = (HumanProposalStatus)r.GetInt32(r.GetOrdinal("status")),
            ParkedPacketId = r.IsDBNull(parkedOrd) ? null : r.GetString(parkedOrd),
            DecisionNote = r.IsDBNull(noteOrd) ? null : r.GetString(noteOrd),
            DecidedBy = r.IsDBNull(byOrd) ? null : r.GetString(byOrd),
            DecidedAt = r.IsDBNull(decidedOrd) ? null : FromIso(r.GetString(decidedOrd)),
            CreatedAt = FromIso(r.GetString(r.GetOrdinal("created_at"))),
            UpdatedAt = FromIso(r.GetString(r.GetOrdinal("updated_at"))),
        };
    }

    private static string ToIso(DateTime v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromIso(string v) => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
