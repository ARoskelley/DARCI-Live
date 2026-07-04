#nullable enable

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// SQLite-backed innovated-knowledge store: entries + an append-only ledger + consumption links. Enforces
/// the governing invariant (an update that raises trust rank needs a human-authored ledger event) and the
/// confidence cap (re-clamped on every write).
/// </summary>
public sealed class SqliteInnovatedKnowledgeStore : IInnovatedKnowledgeStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteInnovatedKnowledgeStore> _logger;

    public SqliteInnovatedKnowledgeStore(string connectionString, ILogger<SqliteInnovatedKnowledgeStore> logger)
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
            CREATE TABLE IF NOT EXISTS innovated_knowledge (
                id TEXT PRIMARY KEY,
                correlation_id TEXT NOT NULL,
                origin_packet_id TEXT NOT NULL,
                hypothesis TEXT NOT NULL,
                topic TEXT NOT NULL,
                intent TEXT NOT NULL,
                cited_facts_json TEXT NOT NULL,
                provenance INTEGER NOT NULL,
                confidence_score REAL NOT NULL,
                confidence_note TEXT NULL,
                success_count INTEGER NOT NULL DEFAULT 0,
                failure_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_innovated_correlation ON innovated_knowledge(correlation_id);
            CREATE INDEX IF NOT EXISTS ix_innovated_provenance ON innovated_knowledge(provenance, updated_at);

            CREATE TABLE IF NOT EXISTS innovated_revisions (
                entry_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                at TEXT NOT NULL,
                kind INTEGER NOT NULL,
                change TEXT NOT NULL,
                provenance INTEGER NOT NULL,
                confidence_score REAL NOT NULL,
                confidence_note TEXT NULL,
                correlation_root TEXT NULL,
                weight REAL NOT NULL DEFAULT 1,
                campaign_id TEXT NULL,
                note TEXT NULL,
                PRIMARY KEY (entry_id, seq)
            );

            CREATE TABLE IF NOT EXISTS innovated_consumption (
                entry_id TEXT NOT NULL,
                correlation_root TEXT NOT NULL,
                outcome INTEGER NOT NULL DEFAULT 0,
                weight REAL NOT NULL DEFAULT 1,
                campaign_id TEXT NULL,
                served_at TEXT NOT NULL,
                resolved_at TEXT NULL,
                PRIMARY KEY (entry_id, correlation_root)
            );
            CREATE INDEX IF NOT EXISTS ix_innovated_consumption_root ON innovated_consumption(correlation_root);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Innovated-knowledge store initialized.");
    }

    public async Task AddAsync(InnovatedKnowledgeRecord record, CancellationToken ct = default)
    {
        record = record with { Confidence = ProvenancePolicy.Clamp(record.Provenance, record.Confidence) };

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await UpsertEntryAsync(conn, tx, record, ct);
        await InsertRevisionAsync(conn, tx, record.Id, 0,
            new LedgerEvent(LedgerEventKind.Created, "created"), record.Provenance, record.Confidence, ct);

        await tx.CommitAsync(ct);
    }

    public async Task UpdateAsync(InnovatedKnowledgeRecord record, LedgerEvent evt, CancellationToken ct = default)
    {
        record = record with
        {
            Confidence = ProvenancePolicy.Clamp(record.Provenance, record.Confidence),
            UpdatedAt = DateTime.UtcNow,
        };

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // ── INVARIANT GUARD (§0a): an upward trust change requires a human-authored event ──
        var currentRank = await CurrentTrustRankAsync(conn, tx, record.Id, ct);
        if (currentRank is int cr && ProvenancePolicy.TrustRank(record.Provenance) > cr && !Ledger.IsHumanAuthored(evt.Kind))
            throw new InvalidOperationException(
                $"Invariant violation: raising trust to {record.Provenance} (rank {cr} → {ProvenancePolicy.TrustRank(record.Provenance)}) " +
                $"requires a human-authored ledger event, not '{evt.Kind}'.");

        var nextSeq = await NextSeqAsync(conn, tx, record.Id, ct);
        await UpsertEntryAsync(conn, tx, record, ct);
        await InsertRevisionAsync(conn, tx, record.Id, nextSeq, evt, record.Provenance, record.Confidence, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<bool> RevertToRevisionAsync(string entryId, int revisionSeq, CancellationToken ct = default)
    {
        var entry = await GetAsync(entryId, ct);
        if (entry is null) return false;
        var target = (await GetRevisionsAsync(entryId, ct)).FirstOrDefault(r => r.Seq == revisionSeq);
        if (target is null) return false;

        // A revert is an explicit human/admin action; tag it human-authored so restoring higher trust is
        // honest under the invariant.
        var kind = ProvenancePolicy.TrustRank(target.Provenance) > ProvenancePolicy.TrustRank(entry.Provenance)
            ? LedgerEventKind.HumanConfirmPromotion
            : LedgerEventKind.HumanRetract;

        await UpdateAsync(
            entry with { Provenance = target.Provenance, Confidence = target.Confidence },
            new LedgerEvent(kind, $"reverted to rev {revisionSeq}", Note: target.Change), ct);
        return true;
    }

    // ── Consumption links ──

    public async Task RecordConsumptionAsync(string entryId, string correlationRoot, double weight = 1.0, string? campaignId = null, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO innovated_consumption (entry_id, correlation_root, outcome, weight, campaign_id, served_at)
            VALUES ($id, $root, 0, $w, $cid, $at)
            """;
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$root", correlationRoot);
        cmd.Parameters.AddWithValue("$w", weight);
        cmd.Parameters.AddWithValue("$cid", (object?)campaignId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", ToIso(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetEntriesByConsumptionRootAsync(string correlationRoot, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT k.* FROM innovated_knowledge k
            JOIN innovated_consumption c ON c.entry_id = k.id
            WHERE c.correlation_root = $root
            """;
        cmd.Parameters.AddWithValue("$root", correlationRoot);
        return await ReadAll(cmd, ct);
    }

    public async Task<bool> ResolveConsumptionAsync(string entryId, string correlationRoot, ConsumptionOutcome outcome, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Only resolve links still Pending — a retry on an already-resolved root affects 0 rows (collapse).
        cmd.CommandText = """
            UPDATE innovated_consumption SET outcome = $o, resolved_at = $at
            WHERE entry_id = $id AND correlation_root = $root AND outcome = 0
            """;
        cmd.Parameters.AddWithValue("$o", (int)outcome);
        cmd.Parameters.AddWithValue("$at", ToIso(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$root", correlationRoot);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<(int Successes, int Failures)> CountDistinctOutcomesAsync(string entryId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              SUM(CASE WHEN outcome = 1 THEN 1 ELSE 0 END),
              SUM(CASE WHEN outcome = 2 THEN 1 ELSE 0 END)
            FROM innovated_consumption WHERE entry_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", entryId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (0, 0);
        var s = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var f = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        return (s, f);
    }

    public async Task<IReadOnlyList<InnovatedConsumption>> GetConsumptionsAsync(string entryId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM innovated_consumption WHERE entry_id = $id ORDER BY served_at";
        cmd.Parameters.AddWithValue("$id", entryId);
        var list = new List<InnovatedConsumption>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var cidOrd = reader.GetOrdinal("campaign_id");
            var resOrd = reader.GetOrdinal("resolved_at");
            list.Add(new InnovatedConsumption
            {
                EntryId = reader.GetString(reader.GetOrdinal("entry_id")),
                CorrelationRoot = reader.GetString(reader.GetOrdinal("correlation_root")),
                Outcome = (ConsumptionOutcome)reader.GetInt32(reader.GetOrdinal("outcome")),
                Weight = reader.GetDouble(reader.GetOrdinal("weight")),
                CampaignId = reader.IsDBNull(cidOrd) ? null : reader.GetString(cidOrd),
                ServedAt = FromIso(reader.GetString(reader.GetOrdinal("served_at"))),
                ResolvedAt = reader.IsDBNull(resOrd) ? null : FromIso(reader.GetString(resOrd)),
            });
        }
        return list;
    }

    // ── Reads ──

    public async Task<InnovatedKnowledgeRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM innovated_knowledge WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM innovated_knowledge WHERE correlation_id = $corr ORDER BY created_at";
        cmd.Parameters.AddWithValue("$corr", correlationId);
        return await ReadAll(cmd, ct);
    }

    public async Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetByProvenanceAsync(Provenance provenance, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM innovated_knowledge WHERE provenance = $p ORDER BY updated_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$p", (int)provenance);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadAll(cmd, ct);
    }

    public async Task<IReadOnlyList<InnovatedRevision>> GetRevisionsAsync(string entryId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM innovated_revisions WHERE entry_id = $id ORDER BY seq";
        cmd.Parameters.AddWithValue("$id", entryId);
        var list = new List<InnovatedRevision>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var noteOrd = reader.GetOrdinal("note");
            var cnoteOrd = reader.GetOrdinal("confidence_note");
            var rootOrd = reader.GetOrdinal("correlation_root");
            var campOrd = reader.GetOrdinal("campaign_id");
            list.Add(new InnovatedRevision
            {
                EntryId = reader.GetString(reader.GetOrdinal("entry_id")),
                Seq = reader.GetInt32(reader.GetOrdinal("seq")),
                At = FromIso(reader.GetString(reader.GetOrdinal("at"))),
                Kind = (LedgerEventKind)reader.GetInt32(reader.GetOrdinal("kind")),
                Change = reader.GetString(reader.GetOrdinal("change")),
                Provenance = (Provenance)reader.GetInt32(reader.GetOrdinal("provenance")),
                Confidence = new Confidence(reader.GetDouble(reader.GetOrdinal("confidence_score")),
                    reader.IsDBNull(cnoteOrd) ? null : reader.GetString(cnoteOrd)),
                CorrelationRoot = reader.IsDBNull(rootOrd) ? null : reader.GetString(rootOrd),
                Weight = reader.GetDouble(reader.GetOrdinal("weight")),
                CampaignId = reader.IsDBNull(campOrd) ? null : reader.GetString(campOrd),
                Note = reader.IsDBNull(noteOrd) ? null : reader.GetString(noteOrd),
            });
        }
        return list;
    }

    // ── helpers ──

    private static async Task<int?> CurrentTrustRankAsync(SqliteConnection conn, SqliteTransaction tx, string id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT provenance FROM innovated_knowledge WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var val = await cmd.ExecuteScalarAsync(ct);
        return val is null or DBNull ? null : ProvenancePolicy.TrustRank((Provenance)Convert.ToInt32(val, CultureInfo.InvariantCulture));
    }

    private static async Task UpsertEntryAsync(SqliteConnection conn, SqliteTransaction tx, InnovatedKnowledgeRecord r, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO innovated_knowledge
                (id, correlation_id, origin_packet_id, hypothesis, topic, intent, cited_facts_json,
                 provenance, confidence_score, confidence_note, success_count, failure_count, created_at, updated_at)
            VALUES
                ($id, $corr, $pkt, $hyp, $topic, $intent, $facts,
                 $prov, $cscore, $cnote, $sc, $fc, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                correlation_id = excluded.correlation_id, origin_packet_id = excluded.origin_packet_id,
                hypothesis = excluded.hypothesis, topic = excluded.topic, intent = excluded.intent,
                cited_facts_json = excluded.cited_facts_json, provenance = excluded.provenance,
                confidence_score = excluded.confidence_score, confidence_note = excluded.confidence_note,
                success_count = excluded.success_count, failure_count = excluded.failure_count,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$corr", r.CorrelationId);
        cmd.Parameters.AddWithValue("$pkt", r.OriginPacketId);
        cmd.Parameters.AddWithValue("$hyp", r.Hypothesis);
        cmd.Parameters.AddWithValue("$topic", r.Topic);
        cmd.Parameters.AddWithValue("$intent", r.Intent);
        cmd.Parameters.AddWithValue("$facts", JsonSerializer.Serialize(r.CitedFactIds));
        cmd.Parameters.AddWithValue("$prov", (int)r.Provenance);
        cmd.Parameters.AddWithValue("$cscore", r.Confidence.Score);
        cmd.Parameters.AddWithValue("$cnote", (object?)r.Confidence.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sc", r.SuccessCount);
        cmd.Parameters.AddWithValue("$fc", r.FailureCount);
        cmd.Parameters.AddWithValue("$created", ToIso(r.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToIso(r.UpdatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertRevisionAsync(SqliteConnection conn, SqliteTransaction tx, string entryId, int seq,
        LedgerEvent evt, Provenance provenance, Confidence confidence, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO innovated_revisions
                (entry_id, seq, at, kind, change, provenance, confidence_score, confidence_note, correlation_root, weight, campaign_id, note)
            VALUES ($id, $seq, $at, $kind, $change, $prov, $cscore, $cnote, $root, $w, $cid, $note)
            """;
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$at", ToIso(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$kind", (int)evt.Kind);
        cmd.Parameters.AddWithValue("$change", evt.Change);
        cmd.Parameters.AddWithValue("$prov", (int)provenance);
        cmd.Parameters.AddWithValue("$cscore", confidence.Score);
        cmd.Parameters.AddWithValue("$cnote", (object?)confidence.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$root", (object?)evt.CorrelationRoot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", evt.Weight);
        cmd.Parameters.AddWithValue("$cid", (object?)evt.CampaignId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)evt.Note ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> NextSeqAsync(SqliteConnection conn, SqliteTransaction tx, string entryId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(seq), -1) + 1 FROM innovated_revisions WHERE entry_id = $id";
        cmd.Parameters.AddWithValue("$id", entryId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<InnovatedKnowledgeRecord>> ReadAll(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<InnovatedKnowledgeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Map(reader));
        return list;
    }

    private static InnovatedKnowledgeRecord Map(SqliteDataReader r)
    {
        var cnoteOrd = r.GetOrdinal("confidence_note");
        return new InnovatedKnowledgeRecord
        {
            Id = r.GetString(r.GetOrdinal("id")),
            CorrelationId = r.GetString(r.GetOrdinal("correlation_id")),
            OriginPacketId = r.GetString(r.GetOrdinal("origin_packet_id")),
            Hypothesis = r.GetString(r.GetOrdinal("hypothesis")),
            Topic = r.GetString(r.GetOrdinal("topic")),
            Intent = r.GetString(r.GetOrdinal("intent")),
            CitedFactIds = DeserializeList(r.GetString(r.GetOrdinal("cited_facts_json"))),
            Provenance = (Provenance)r.GetInt32(r.GetOrdinal("provenance")),
            Confidence = new Confidence(r.GetDouble(r.GetOrdinal("confidence_score")),
                r.IsDBNull(cnoteOrd) ? null : r.GetString(cnoteOrd)),
            SuccessCount = r.GetInt32(r.GetOrdinal("success_count")),
            FailureCount = r.GetInt32(r.GetOrdinal("failure_count")),
            CreatedAt = FromIso(r.GetString(r.GetOrdinal("created_at"))),
            UpdatedAt = FromIso(r.GetString(r.GetOrdinal("updated_at"))),
        };
    }

    private static List<string> DeserializeList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); } catch { return new(); }
    }

    private static string ToIso(DateTime v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromIso(string v) => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
