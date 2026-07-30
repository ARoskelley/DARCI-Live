#nullable enable

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// SQLite-backed validation-campaign store. The pre-registered protocol is serialized once; step evidence
/// is upserted per (campaign, step) as child packets resolve. The verdict is always RECOMPUTED from the
/// pinned protocol × evidence (never trusted from a stored field), keeping it a pure function of the record.
/// </summary>
public sealed class SqliteValidationCampaignStore : IValidationCampaignStore
{
    /// <summary>
    /// SU5: <see cref="ValidationStep"/> embeds <see cref="Capability"/>/<see cref="NodeId"/>, which were
    /// serialized as bare enum ORDINALS — a form that cannot express an external node's capability. The
    /// converter writes them as canonical strings from now on and still READS the numeric form, so campaigns
    /// persisted before this change keep loading. Non-destructive, like the column migration.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _connectionString;
    private readonly ILogger<SqliteValidationCampaignStore> _logger;

    public SqliteValidationCampaignStore(string connectionString, ILogger<SqliteValidationCampaignStore> logger)
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
            CREATE TABLE IF NOT EXISTS validation_campaigns (
                id TEXT PRIMARY KEY,
                entry_id TEXT NOT NULL,
                hypothesis_revision_seq INTEGER NOT NULL,
                hypothesis_snapshot TEXT NOT NULL,
                target_stage INTEGER NOT NULL,
                domain INTEGER NOT NULL,
                protocol_json TEXT NOT NULL,
                authorization_json TEXT NULL,
                status INTEGER NOT NULL,
                correlation_id TEXT NOT NULL,
                promotion_preauthorized INTEGER NOT NULL DEFAULT 0,
                priority INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_campaigns_entry ON validation_campaigns(entry_id);
            CREATE INDEX IF NOT EXISTS ix_campaigns_status ON validation_campaigns(status, created_at);
            CREATE INDEX IF NOT EXISTS ix_campaigns_correlation ON validation_campaigns(correlation_id);

            CREATE TABLE IF NOT EXISTS validation_step_evidence (
                campaign_id TEXT NOT NULL,
                step_id TEXT NOT NULL,
                outcome INTEGER NOT NULL,
                measurements_json TEXT NOT NULL,
                note TEXT NULL,
                child_packet_id TEXT NULL,
                at TEXT NOT NULL,
                PRIMARY KEY (campaign_id, step_id)
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Validation-campaign store initialized.");
    }

    public async Task AddAsync(ValidationCampaign campaign, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO validation_campaigns
                (id, entry_id, hypothesis_revision_seq, hypothesis_snapshot, target_stage, domain,
                 protocol_json, authorization_json, status, correlation_id, promotion_preauthorized, priority, created_at, updated_at)
            VALUES
                ($id, $entry, $seq, $snap, $stage, $domain,
                 $proto, $auth, $status, $corr, $preauth, $priority, $created, $updated)
            """;
        BindCampaign(cmd, campaign);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task UpdateAsync(ValidationCampaign campaign, CancellationToken ct = default)
        => AddAsync(campaign with { UpdatedAt = DateTime.UtcNow }, ct);

    public async Task<ValidationCampaign?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM validation_campaigns WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<ValidationCampaign>> GetByEntryAsync(string entryId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM validation_campaigns WHERE entry_id = $entry ORDER BY created_at";
        cmd.Parameters.AddWithValue("$entry", entryId);
        return await ReadAll(cmd, ct);
    }

    public async Task<IReadOnlyList<ValidationCampaign>> GetByStatusAsync(CampaignStatus status, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM validation_campaigns WHERE status = $s ORDER BY created_at LIMIT $limit";
        cmd.Parameters.AddWithValue("$s", (int)status);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadAll(cmd, ct);
    }

    public async Task<IReadOnlyList<ValidationCampaign>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM validation_campaigns WHERE correlation_id = $corr ORDER BY created_at";
        cmd.Parameters.AddWithValue("$corr", correlationId);
        return await ReadAll(cmd, ct);
    }

    public async Task RecordStepEvidenceAsync(string campaignId, StepEvidence evidence, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO validation_step_evidence
                (campaign_id, step_id, outcome, measurements_json, note, child_packet_id, at)
            VALUES ($cid, $sid, $outcome, $meas, $note, $pkt, $at)
            """;
        cmd.Parameters.AddWithValue("$cid", campaignId);
        cmd.Parameters.AddWithValue("$sid", evidence.StepId);
        cmd.Parameters.AddWithValue("$outcome", (int)evidence.Outcome);
        cmd.Parameters.AddWithValue("$meas", JsonSerializer.Serialize(evidence.Measurements, JsonOpts));
        cmd.Parameters.AddWithValue("$note", (object?)evidence.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pkt", (object?)evidence.ChildPacketId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", ToIso(evidence.At == default ? DateTime.UtcNow : evidence.At));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<StepEvidence>> GetStepEvidenceAsync(string campaignId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM validation_step_evidence WHERE campaign_id = $cid ORDER BY at";
        cmd.Parameters.AddWithValue("$cid", campaignId);
        var list = new List<StepEvidence>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var noteOrd = reader.GetOrdinal("note");
            var pktOrd = reader.GetOrdinal("child_packet_id");
            list.Add(new StepEvidence(
                reader.GetString(reader.GetOrdinal("step_id")),
                (ValidationStepOutcome)reader.GetInt32(reader.GetOrdinal("outcome")),
                DeserializeMeasurements(reader.GetString(reader.GetOrdinal("measurements_json"))),
                reader.IsDBNull(noteOrd) ? null : reader.GetString(noteOrd),
                reader.IsDBNull(pktOrd) ? null : reader.GetString(pktOrd),
                FromIso(reader.GetString(reader.GetOrdinal("at")))));
        }
        return list;
    }

    public async Task<CampaignVerdict> ComputeVerdictAsync(string campaignId, CancellationToken ct = default)
    {
        var campaign = await GetAsync(campaignId, ct);
        if (campaign is null) return CampaignVerdict.Pending;
        var evidence = await GetStepEvidenceAsync(campaignId, ct);
        return CampaignProtocol.Evaluate(campaign, evidence);
    }

    // ── helpers ──

    private static void BindCampaign(SqliteCommand cmd, ValidationCampaign c)
    {
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$entry", c.EntryId);
        cmd.Parameters.AddWithValue("$seq", c.HypothesisRevisionSeq);
        cmd.Parameters.AddWithValue("$snap", c.HypothesisSnapshot);
        cmd.Parameters.AddWithValue("$stage", (int)c.TargetStage);
        cmd.Parameters.AddWithValue("$domain", (int)c.Domain);
        cmd.Parameters.AddWithValue("$proto", JsonSerializer.Serialize(c.Protocol, JsonOpts));
        cmd.Parameters.AddWithValue("$auth", c.Authorization is null ? DBNull.Value : JsonSerializer.Serialize(c.Authorization, JsonOpts));
        cmd.Parameters.AddWithValue("$status", (int)c.Status);
        cmd.Parameters.AddWithValue("$corr", c.CorrelationId);
        cmd.Parameters.AddWithValue("$preauth", c.PromotionPreauthorized ? 1 : 0);
        cmd.Parameters.AddWithValue("$priority", (int)c.Priority);
        cmd.Parameters.AddWithValue("$created", ToIso(c.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", ToIso(c.UpdatedAt));
    }

    private static async Task<IReadOnlyList<ValidationCampaign>> ReadAll(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<ValidationCampaign>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Map(reader));
        return list;
    }

    private static ValidationCampaign Map(SqliteDataReader r)
    {
        var authOrd = r.GetOrdinal("authorization_json");
        return new ValidationCampaign
        {
            Id = r.GetString(r.GetOrdinal("id")),
            EntryId = r.GetString(r.GetOrdinal("entry_id")),
            HypothesisRevisionSeq = r.GetInt32(r.GetOrdinal("hypothesis_revision_seq")),
            HypothesisSnapshot = r.GetString(r.GetOrdinal("hypothesis_snapshot")),
            TargetStage = (Provenance)r.GetInt32(r.GetOrdinal("target_stage")),
            Domain = (KnowledgeDomain)r.GetInt32(r.GetOrdinal("domain")),
            Protocol = DeserializeProtocol(r.GetString(r.GetOrdinal("protocol_json"))),
            Authorization = r.IsDBNull(authOrd) ? null : JsonSerializer.Deserialize<CampaignAuthorization>(r.GetString(authOrd), JsonOpts),
            Status = (CampaignStatus)r.GetInt32(r.GetOrdinal("status")),
            CorrelationId = r.GetString(r.GetOrdinal("correlation_id")),
            PromotionPreauthorized = r.GetInt32(r.GetOrdinal("promotion_preauthorized")) != 0,
            Priority = (CampaignPriority)r.GetInt32(r.GetOrdinal("priority")),
            CreatedAt = FromIso(r.GetString(r.GetOrdinal("created_at"))),
            UpdatedAt = FromIso(r.GetString(r.GetOrdinal("updated_at"))),
        };
    }

    private static IReadOnlyList<ValidationStep> DeserializeProtocol(string json)
    {
        // MUST use the same options as the writer: JsonOpts writes enums as strings, and default options
        // cannot read those back. Asymmetric options here silently yield an EMPTY protocol, which would make
        // every campaign verdict read as Pending.
        try { return JsonSerializer.Deserialize<List<ValidationStep>>(json, JsonOpts) ?? new(); }
        catch { return new List<ValidationStep>(); }
    }

    private static IReadOnlyDictionary<string, double> DeserializeMeasurements(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOpts) ?? new(); }
        catch { return new Dictionary<string, double>(); }
    }

    private static string ToIso(DateTime v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromIso(string v) => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
