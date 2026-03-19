#nullable enable

using System.Text.Json;
using Dapper;
using Darci.Memory.Confidence.Models;
using Darci.Memory.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Memory.Confidence;

public sealed class ConfidenceTracker : IConfidenceTracker
{
    private readonly string _connectionString;
    private readonly IKnowledgeGraph _graph;
    private readonly ILogger<ConfidenceTracker> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Dictionary<string, float> SourceQualityDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pubmed"] = 0.85f,
        ["clinical_trial"] = 0.90f,
        ["systematic_review"] = 0.92f,
        ["textbook"] = 0.80f,
        ["web_news"] = 0.45f,
        ["web_general"] = 0.35f,
        ["llm"] = 0.30f,
        ["user"] = 0.60f,
        ["reasoning"] = 0.40f,
        ["peer_reviewed"] = 0.88f
    };

    public ConfidenceTracker(
        string connectionString,
        IKnowledgeGraph graph,
        ILogger<ConfidenceTracker>? logger = null)
    {
        _connectionString = connectionString;
        _graph = graph;
        _logger = logger ?? NullLogger<ConfidenceTracker>.Instance;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS confidence_claims (
              id                TEXT PRIMARY KEY,
              statement         TEXT NOT NULL,
              domain            TEXT NOT NULL DEFAULT 'general',
              entity_ids        TEXT NOT NULL DEFAULT '[]',
              relation_ids      TEXT NOT NULL DEFAULT '[]',
              confidence        REAL NOT NULL DEFAULT 0.5,
              source_quality    REAL NOT NULL DEFAULT 0.5,
              corroboration     REAL NOT NULL DEFAULT 0.0,
              contradiction     REAL NOT NULL DEFAULT 0.0,
              recency_weight    REAL NOT NULL DEFAULT 1.0,
              source_type       TEXT NOT NULL DEFAULT 'llm',
              source_ref        TEXT,
              is_uncertain      INTEGER NOT NULL DEFAULT 0,
              created_at        TEXT NOT NULL,
              updated_at        TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS confidence_contradictions (
              id           TEXT PRIMARY KEY,
              claim_a_id   TEXT NOT NULL REFERENCES confidence_claims(id),
              claim_b_id   TEXT NOT NULL REFERENCES confidence_claims(id),
              severity     REAL NOT NULL DEFAULT 0.5,
              resolved     INTEGER NOT NULL DEFAULT 0,
              resolution   TEXT,
              created_at   TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_claims_domain ON confidence_claims(domain);
            CREATE INDEX IF NOT EXISTS ix_claims_conf   ON confidence_claims(confidence);
            CREATE INDEX IF NOT EXISTS ix_contradictions_claim_a ON confidence_contradictions(claim_a_id);
            CREATE INDEX IF NOT EXISTS ix_contradictions_claim_b ON confidence_contradictions(claim_b_id);
            """,
            cancellationToken: ct));

        _logger.LogInformation("ConfidenceTracker tables ready.");
    }

    public async Task<KnowledgeClaim> AddClaimAsync(
        string statement,
        string domain,
        string sourceType,
        string? sourceRef = null,
        float sourceQuality = 0.5f,
        string[]? entityIds = null,
        string[]? relationIds = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var effectiveSourceQuality = ResolveSourceQuality(sourceType, sourceQuality);

        var claim = new KnowledgeClaim
        {
            Id = Guid.NewGuid().ToString("N"),
            Statement = statement.Trim(),
            Domain = string.IsNullOrWhiteSpace(domain) ? "general" : domain.Trim().ToLowerInvariant(),
            EntityIds = NormalizeIds(entityIds),
            RelationIds = NormalizeIds(relationIds),
            SourceQuality = effectiveSourceQuality,
            Corroboration = 0f,
            Contradiction = 0f,
            RecencyWeight = 1.0f,
            SourceType = string.IsNullOrWhiteSpace(sourceType) ? "llm" : sourceType.Trim().ToLowerInvariant(),
            SourceRef = sourceRef,
            CreatedAt = now,
            UpdatedAt = now
        };

        var computed = ComputeConfidence(claim);
        claim = claim with
        {
            Confidence = computed,
            IsUncertain = computed < 0.4f
        };

        using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO confidence_claims
                (id, statement, domain, entity_ids, relation_ids, confidence, source_quality, corroboration,
                 contradiction, recency_weight, source_type, source_ref, is_uncertain, created_at, updated_at)
            VALUES
                (@Id, @Statement, @Domain, @EntityIds, @RelationIds, @Confidence, @SourceQuality, @Corroboration,
                 @Contradiction, @RecencyWeight, @SourceType, @SourceRef, @IsUncertain, @CreatedAt, @UpdatedAt)
            """,
            new
            {
                claim.Id,
                claim.Statement,
                claim.Domain,
                EntityIds = Serialize(claim.EntityIds),
                RelationIds = Serialize(claim.RelationIds),
                claim.Confidence,
                claim.SourceQuality,
                claim.Corroboration,
                claim.Contradiction,
                claim.RecencyWeight,
                claim.SourceType,
                claim.SourceRef,
                IsUncertain = claim.IsUncertain ? 1 : 0,
                CreatedAt = claim.CreatedAt.ToString("O"),
                UpdatedAt = claim.UpdatedAt.ToString("O")
            },
            cancellationToken: ct));

        await PropagateEntityConfidenceAsync(conn, claim.EntityIds, ct);
        return claim;
    }

    public async Task<KnowledgeClaim?> GetClaimAsync(string id, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ClaimRow>(new CommandDefinition(
            "SELECT * FROM confidence_claims WHERE id = @Id",
            new { Id = id },
            cancellationToken: ct));

        return row is null ? null : MapClaim(row);
    }

    public async Task<IReadOnlyList<KnowledgeClaim>> GetClaimsForEntityAsync(
        string entityId,
        int limit = 50,
        CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<ClaimRow>(new CommandDefinition(
            """
            SELECT *
            FROM confidence_claims
            WHERE entity_ids LIKE @Pattern
            ORDER BY confidence DESC, updated_at DESC
            LIMIT @Limit
            """,
            new { Pattern = BuildJsonLikePattern(entityId), Limit = Math.Max(1, limit) },
            cancellationToken: ct));

        return rows.Select(MapClaim).ToList();
    }

    public async Task<IReadOnlyList<KnowledgeClaim>> GetUncertainClaimsAsync(
        float threshold = 0.4f,
        string? domain = null,
        int limit = 30,
        CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<ClaimRow>(new CommandDefinition(
            """
            SELECT *
            FROM confidence_claims
            WHERE confidence < @Threshold
              AND (@Domain IS NULL OR domain = @Domain)
            ORDER BY confidence ASC, updated_at DESC
            LIMIT @Limit
            """,
            new
            {
                Threshold = Math.Clamp(threshold, 0f, 1f),
                Domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim().ToLowerInvariant(),
                Limit = Math.Max(1, limit)
            },
            cancellationToken: ct));

        return rows.Select(MapClaim).ToList();
    }

    public async Task CorroborateAsync(
        string claimId,
        string sourceType,
        string? sourceRef = null,
        float sourcequality = 0.5f,
        CancellationToken ct = default)
    {
        var claim = await GetClaimAsync(claimId, ct);
        if (claim is null)
        {
            return;
        }

        var effectiveSourceQuality = ResolveSourceQuality(sourceType, sourcequality);
        var corroborationBoost = Math.Clamp(0.15f + (effectiveSourceQuality * 0.35f), 0.05f, 0.45f);

        var updated = claim with
        {
            Corroboration = Math.Min(1f, claim.Corroboration + corroborationBoost),
            SourceQuality = Math.Clamp((claim.SourceQuality + effectiveSourceQuality) / 2f, 0f, 1f),
            SourceRef = sourceRef ?? claim.SourceRef,
            UpdatedAt = DateTime.UtcNow
        };

        updated = updated with
        {
            Confidence = ComputeConfidence(updated),
            IsUncertain = ComputeConfidence(updated) < 0.4f
        };

        using var conn = await OpenAsync(ct);
        await UpdateClaimAsync(conn, updated, ct);
        await PropagateEntityConfidenceAsync(conn, updated.EntityIds, ct);
    }

    public async Task<Contradiction> RecordContradictionAsync(
        string claimAId,
        string claimBId,
        float severity,
        CancellationToken ct = default)
    {
        var contradiction = new Contradiction
        {
            Id = Guid.NewGuid().ToString("N"),
            ClaimAId = claimAId,
            ClaimBId = claimBId,
            Severity = Math.Clamp(severity, 0f, 1f),
            CreatedAt = DateTime.UtcNow
        };

        using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO confidence_contradictions
                (id, claim_a_id, claim_b_id, severity, resolved, resolution, created_at)
            VALUES
                (@Id, @ClaimAId, @ClaimBId, @Severity, @Resolved, @Resolution, @CreatedAt)
            """,
            new
            {
                contradiction.Id,
                contradiction.ClaimAId,
                contradiction.ClaimBId,
                contradiction.Severity,
                Resolved = contradiction.Resolved ? 1 : 0,
                contradiction.Resolution,
                CreatedAt = contradiction.CreatedAt.ToString("O")
            },
            cancellationToken: ct));

        await ApplyContradictionPenaltyAsync(conn, claimAId, contradiction.Severity, ct);
        await ApplyContradictionPenaltyAsync(conn, claimBId, contradiction.Severity, ct);
        return contradiction;
    }

    public async Task<IReadOnlyList<Contradiction>> GetUnresolvedContradictionsAsync(
        string? domain = null,
        CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<ContradictionRow>(new CommandDefinition(
            domain is null
                ? """
                  SELECT *
                  FROM confidence_contradictions
                  WHERE resolved = 0
                  ORDER BY severity DESC, created_at DESC
                  """
                : """
                  SELECT cc.*
                  FROM confidence_contradictions cc
                  INNER JOIN confidence_claims ca ON ca.id = cc.claim_a_id
                  INNER JOIN confidence_claims cb ON cb.id = cc.claim_b_id
                  WHERE cc.resolved = 0
                    AND (ca.domain = @Domain OR cb.domain = @Domain)
                  ORDER BY cc.severity DESC, cc.created_at DESC
                  """,
            new { Domain = domain },
            cancellationToken: ct));

        return rows.Select(MapContradiction).ToList();
    }

    public async Task ResolveContradictionAsync(
        string contradictionId,
        string resolution,
        CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE confidence_contradictions
            SET resolved = 1,
                resolution = @Resolution
            WHERE id = @Id
            """,
            new { Id = contradictionId, Resolution = resolution },
            cancellationToken: ct));
    }

    public async Task<SynthesisResult> SynthesizeAsync(
        string question,
        string? domain = null,
        Func<string, Task<List<float>>>? getEmbedding = null,
        CancellationToken ct = default)
    {
        var claims = new List<KnowledgeClaim>();
        var claimIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (getEmbedding is not null)
        {
            try
            {
                var embedding = await getEmbedding(question);
                var semanticMatches = await _graph.SemanticSearchAsync(embedding.ToArray(), limit: 5, ct);
                foreach (var match in semanticMatches)
                {
                    entityIds.Add(match.Entity.Id);
                    var entityClaims = await GetClaimsForEntityAsync(match.Entity.Id, limit: 10, ct);
                    foreach (var claim in entityClaims)
                    {
                        if (claimIds.Add(claim.Id))
                        {
                            claims.Add(claim);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Semantic confidence synthesis lookup failed for question: {Question}", question);
            }
        }

        using var conn = await OpenAsync(ct);
        var textRows = await conn.QueryAsync<ClaimRow>(new CommandDefinition(
            """
            SELECT *
            FROM confidence_claims
            WHERE statement LIKE @Pattern
              AND (@Domain IS NULL OR domain = @Domain)
            ORDER BY confidence DESC, updated_at DESC
            LIMIT 20
            """,
            new
            {
                Pattern = $"%{question.Trim()}%",
                Domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim().ToLowerInvariant()
            },
            cancellationToken: ct));

        foreach (var row in textRows)
        {
            var claim = MapClaim(row);
            if (claimIds.Add(claim.Id))
            {
                claims.Add(claim);
            }
        }

        var selectedClaims = claims
            .OrderByDescending(claim => claim.Confidence)
            .Take(15)
            .ToList();

        foreach (var claim in selectedClaims)
        {
            foreach (var entityId in claim.EntityIds)
            {
                entityIds.Add(entityId);
            }
        }

        var contradictions = (await GetUnresolvedContradictionsAsync(domain, ct))
            .Where(contradiction =>
                selectedClaims.Any(claim => claim.Id == contradiction.ClaimAId || claim.Id == contradiction.ClaimBId))
            .ToList();

        var aggregate = ComputeAggregateConfidence(selectedClaims);
        var reasons = new List<string>();
        if (selectedClaims.Count == 0)
        {
            reasons.Add("No supporting claims were found.");
        }

        if (aggregate < 0.45f)
        {
            reasons.Add($"Aggregate confidence is only {aggregate:P0}.");
        }

        if (contradictions.Count > 0)
        {
            reasons.Add($"There are {contradictions.Count} unresolved contradictions.");
        }

        return new SynthesisResult
        {
            Question = question,
            AggregateConf = aggregate,
            IsUncertain = aggregate < 0.45f || contradictions.Count > 0,
            SupportingClaims = selectedClaims,
            ActiveContradictions = contradictions,
            UncertaintyReason = reasons.Count == 0 ? "" : string.Join(" ", reasons)
        };
    }

    public async Task DecayAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30).ToString("O");

        using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE confidence_claims
            SET recency_weight = MAX(0.1, recency_weight * 0.97)
            WHERE created_at < @Cutoff
            """,
            new { Cutoff = cutoff },
            cancellationToken: ct));

        var affectedRows = await conn.QueryAsync<ClaimRow>(new CommandDefinition(
            """
            SELECT *
            FROM confidence_claims
            WHERE created_at < @Cutoff
            """,
            new { Cutoff = cutoff },
            cancellationToken: ct));

        var affectedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in affectedRows)
        {
            var claim = MapClaim(row);
            var updated = claim with
            {
                Confidence = ComputeConfidence(claim),
                IsUncertain = ComputeConfidence(claim) < 0.4f,
                UpdatedAt = DateTime.UtcNow
            };

            await UpdateClaimAsync(conn, updated, ct);
            foreach (var entityId in updated.EntityIds)
            {
                affectedEntities.Add(entityId);
            }
        }

        await PropagateEntityConfidenceAsync(conn, affectedEntities, ct);
    }

    private static float ComputeConfidence(KnowledgeClaim claim)
    {
        var corr = Math.Min(1f, claim.Corroboration);
        var contr = claim.Contradiction;
        var baseScore = claim.SourceQuality * 0.4f
            + corr * 0.4f
            - contr * 0.3f;

        var score = baseScore * claim.RecencyWeight;
        return Math.Clamp(score, 0f, 1f);
    }

    private async Task ApplyContradictionPenaltyAsync(
        SqliteConnection conn,
        string claimId,
        float severity,
        CancellationToken ct)
    {
        var claim = await GetClaimAsync(claimId, ct);
        if (claim is null)
        {
            return;
        }

        var updated = claim with
        {
            Contradiction = Math.Max(claim.Contradiction, Math.Clamp(severity, 0f, 1f)),
            UpdatedAt = DateTime.UtcNow
        };

        updated = updated with
        {
            Confidence = ComputeConfidence(updated),
            IsUncertain = ComputeConfidence(updated) < 0.4f
        };

        await UpdateClaimAsync(conn, updated, ct);
        await PropagateEntityConfidenceAsync(conn, updated.EntityIds, ct);
    }

    private async Task UpdateClaimAsync(SqliteConnection conn, KnowledgeClaim claim, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE confidence_claims
            SET statement = @Statement,
                domain = @Domain,
                entity_ids = @EntityIds,
                relation_ids = @RelationIds,
                confidence = @Confidence,
                source_quality = @SourceQuality,
                corroboration = @Corroboration,
                contradiction = @Contradiction,
                recency_weight = @RecencyWeight,
                source_type = @SourceType,
                source_ref = @SourceRef,
                is_uncertain = @IsUncertain,
                updated_at = @UpdatedAt
            WHERE id = @Id
            """,
            new
            {
                claim.Id,
                claim.Statement,
                claim.Domain,
                EntityIds = Serialize(claim.EntityIds),
                RelationIds = Serialize(claim.RelationIds),
                claim.Confidence,
                claim.SourceQuality,
                claim.Corroboration,
                claim.Contradiction,
                claim.RecencyWeight,
                claim.SourceType,
                claim.SourceRef,
                IsUncertain = claim.IsUncertain ? 1 : 0,
                UpdatedAt = claim.UpdatedAt.ToString("O")
            },
            cancellationToken: ct));
    }

    private async Task PropagateEntityConfidenceAsync(
        SqliteConnection conn,
        IEnumerable<string> entityIds,
        CancellationToken ct)
    {
        foreach (var entityId in NormalizeIds(entityIds))
        {
            var aggregate = await conn.QuerySingleAsync<EntityAggregateRow>(new CommandDefinition(
                """
                SELECT COALESCE(AVG(confidence), 0.5) AS average_confidence,
                       COUNT(*) AS source_count
                FROM confidence_claims
                WHERE entity_ids LIKE @Pattern
                """,
                new { Pattern = BuildJsonLikePattern(entityId) },
                cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE kg_entities
                SET confidence = @Confidence,
                    source_count = @SourceCount,
                    updated_at = @UpdatedAt
                WHERE id = @Id
                """,
                new
                {
                    Id = entityId,
                    Confidence = Math.Clamp((float)aggregate.average_confidence, 0f, 1f),
                    SourceCount = aggregate.source_count,
                    UpdatedAt = DateTime.UtcNow.ToString("O")
                },
                cancellationToken: ct));
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static KnowledgeClaim MapClaim(ClaimRow row) => new()
    {
        Id = row.id,
        Statement = row.statement,
        Domain = row.domain,
        EntityIds = DeserializeStringArray(row.entity_ids),
        RelationIds = DeserializeStringArray(row.relation_ids),
        Confidence = (float)row.confidence,
        SourceQuality = (float)row.source_quality,
        Corroboration = (float)row.corroboration,
        Contradiction = (float)row.contradiction,
        RecencyWeight = (float)row.recency_weight,
        SourceType = row.source_type,
        SourceRef = row.source_ref,
        IsUncertain = row.is_uncertain != 0,
        CreatedAt = DateTime.Parse(row.created_at),
        UpdatedAt = DateTime.Parse(row.updated_at)
    };

    private static Contradiction MapContradiction(ContradictionRow row) => new()
    {
        Id = row.id,
        ClaimAId = row.claim_a_id,
        ClaimBId = row.claim_b_id,
        Severity = (float)row.severity,
        Resolved = row.resolved != 0,
        Resolution = row.resolution,
        CreatedAt = DateTime.Parse(row.created_at)
    };

    private static string[] NormalizeIds(IEnumerable<string>? ids)
        => ids?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
           ?? Array.Empty<string>();

    private static float ResolveSourceQuality(string sourceType, float requested)
    {
        if (requested != 0.5f)
        {
            return Math.Clamp(requested, 0f, 1f);
        }

        return SourceQualityDefaults.TryGetValue(sourceType, out var mapped)
            ? mapped
            : Math.Clamp(requested, 0f, 1f);
    }

    private static float ComputeAggregateConfidence(IEnumerable<KnowledgeClaim> claims)
    {
        var list = claims.ToList();
        if (list.Count == 0)
        {
            return 0f;
        }

        var denominator = list.Sum(claim => claim.Confidence);
        if (denominator <= 0f)
        {
            return 0f;
        }

        var numerator = list.Sum(claim => claim.Confidence * claim.Confidence);
        return Math.Clamp(numerator / denominator, 0f, 1f);
    }

    private static string BuildJsonLikePattern(string id) => $"%\"{id}\"%";

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static string[] DeserializeStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, Json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private sealed class ClaimRow
    {
        public string id { get; set; } = "";
        public string statement { get; set; } = "";
        public string domain { get; set; } = "general";
        public string entity_ids { get; set; } = "[]";
        public string relation_ids { get; set; } = "[]";
        public double confidence { get; set; }
        public double source_quality { get; set; }
        public double corroboration { get; set; }
        public double contradiction { get; set; }
        public double recency_weight { get; set; }
        public string source_type { get; set; } = "llm";
        public string? source_ref { get; set; }
        public int is_uncertain { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
    }

    private sealed class ContradictionRow
    {
        public string id { get; set; } = "";
        public string claim_a_id { get; set; } = "";
        public string claim_b_id { get; set; } = "";
        public double severity { get; set; }
        public int resolved { get; set; }
        public string? resolution { get; set; }
        public string created_at { get; set; } = "";
    }

    private sealed class EntityAggregateRow
    {
        public double average_confidence { get; set; }
        public int source_count { get; set; }
    }
}
