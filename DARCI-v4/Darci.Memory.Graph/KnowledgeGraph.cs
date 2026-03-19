#nullable enable

using System.Text.Json;
using Dapper;
using Darci.Memory.Graph.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Memory.Graph;

public sealed class KnowledgeGraph : IKnowledgeGraph
{
    private readonly string _connectionString;
    private readonly ILogger<KnowledgeGraph> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public KnowledgeGraph(string connectionString, ILogger<KnowledgeGraph>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger ?? NullLogger<KnowledgeGraph>.Instance;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS kg_entities (
              id           TEXT PRIMARY KEY,
              name         TEXT NOT NULL,
              entity_type  TEXT NOT NULL,
              domain       TEXT NOT NULL DEFAULT 'general',
              description  TEXT NOT NULL DEFAULT '',
              aliases      TEXT NOT NULL DEFAULT '[]',
              embedding    TEXT,
              confidence   REAL NOT NULL DEFAULT 0.5,
              source_count INTEGER NOT NULL DEFAULT 0,
              created_at   TEXT NOT NULL,
              updated_at   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS kg_relations (
              id              TEXT PRIMARY KEY,
              from_entity_id  TEXT NOT NULL REFERENCES kg_entities(id),
              to_entity_id    TEXT NOT NULL REFERENCES kg_entities(id),
              relation_type   TEXT NOT NULL,
              direction       TEXT NOT NULL DEFAULT 'directed',
              weight          REAL NOT NULL DEFAULT 1.0,
              confidence      REAL NOT NULL DEFAULT 0.5,
              evidence_ids    TEXT NOT NULL DEFAULT '[]',
              created_at      TEXT NOT NULL,
              updated_at      TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_kg_rel_from ON kg_relations(from_entity_id);
            CREATE INDEX IF NOT EXISTS ix_kg_rel_to   ON kg_relations(to_entity_id);
            CREATE INDEX IF NOT EXISTS ix_kg_rel_type ON kg_relations(relation_type);
            CREATE INDEX IF NOT EXISTS ix_kg_entities_name ON kg_entities(name);
            CREATE INDEX IF NOT EXISTS ix_kg_entities_domain ON kg_entities(domain);
            """,
            cancellationToken: ct));

        _logger.LogInformation("KnowledgeGraph tables ready.");
    }

    public async Task<KgEntity> UpsertEntityAsync(
        string name,
        string entityType,
        string domain,
        string? description = null,
        string[]? aliases = null,
        float[]? embedding = null,
        CancellationToken ct = default)
    {
        var normalizedName = name.Trim();
        var now = DateTime.UtcNow;

        using var conn = await OpenAsync(ct);
        var existing = await GetEntityRowByNameAsync(conn, normalizedName, ct);
        if (existing is not null)
        {
            var mergedAliases = MergeAliases(
                DeserializeStringArray(existing.aliases),
                aliases,
                normalizedName);

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE kg_entities
                SET entity_type = @EntityType,
                    domain = @Domain,
                    description = @Description,
                    aliases = @Aliases,
                    embedding = @Embedding,
                    updated_at = @UpdatedAt
                WHERE id = @Id
                """,
                new
                {
                    Id = existing.id,
                    EntityType = entityType,
                    Domain = string.IsNullOrWhiteSpace(domain) ? existing.domain : domain,
                    Description = description ?? existing.description,
                    Aliases = Serialize(mergedAliases),
                    Embedding = embedding is null ? existing.embedding : Serialize(embedding),
                    UpdatedAt = now.ToString("O")
                },
                cancellationToken: ct));

            await RefreshEntityConfidenceAsync(conn, existing.id, ct);
            return (await GetEntityAsync(existing.id, ct))!;
        }

        var entity = new KgEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = normalizedName,
            EntityType = entityType,
            Domain = string.IsNullOrWhiteSpace(domain) ? "general" : domain,
            Description = description ?? "",
            Aliases = MergeAliases(Array.Empty<string>(), aliases, normalizedName),
            Embedding = embedding,
            CreatedAt = now,
            UpdatedAt = now
        };

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO kg_entities
                (id, name, entity_type, domain, description, aliases, embedding, confidence, source_count, created_at, updated_at)
            VALUES
                (@Id, @Name, @EntityType, @Domain, @Description, @Aliases, @Embedding, @Confidence, @SourceCount, @CreatedAt, @UpdatedAt)
            """,
            new
            {
                entity.Id,
                entity.Name,
                entity.EntityType,
                entity.Domain,
                entity.Description,
                Aliases = Serialize(entity.Aliases),
                Embedding = entity.Embedding is null ? null : Serialize(entity.Embedding),
                entity.Confidence,
                entity.SourceCount,
                CreatedAt = entity.CreatedAt.ToString("O"),
                UpdatedAt = entity.UpdatedAt.ToString("O")
            },
            cancellationToken: ct));

        await RefreshEntityConfidenceAsync(conn, entity.Id, ct);
        return (await GetEntityAsync(entity.Id, ct))!;
    }

    public async Task<KgEntity?> GetEntityAsync(string id, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<EntityRow>(new CommandDefinition(
            "SELECT * FROM kg_entities WHERE id = @Id",
            new { Id = id },
            cancellationToken: ct));

        return row is null ? null : MapEntity(row);
    }

    public async Task<KgEntity?> FindEntityByNameAsync(string name, CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        var row = await GetEntityRowByNameAsync(conn, name, ct);
        return row is null ? null : MapEntity(row);
    }

    public async Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(
        string query,
        string? domain = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<KgEntity>();
        }

        using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<EntityRow>(new CommandDefinition(
            """
            SELECT *
            FROM kg_entities
            WHERE (@Domain IS NULL OR domain = @Domain)
              AND (LOWER(name) LIKE LOWER(@Pattern) OR LOWER(aliases) LIKE LOWER(@Pattern))
            ORDER BY updated_at DESC
            LIMIT @Limit
            """,
            new
            {
                Domain = string.IsNullOrWhiteSpace(domain) ? null : domain,
                Pattern = $"%{query.Trim()}%",
                Limit = Math.Max(1, limit)
            },
            cancellationToken: ct));

        return rows
            .Select(MapEntity)
            .OrderByDescending(entity => ComputeTextScore(entity, query))
            .ThenByDescending(entity => entity.UpdatedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<KgEntity>> GetEntitiesByDomainAsync(
        string domain,
        int limit = 100,
        CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<EntityRow>(new CommandDefinition(
            """
            SELECT *
            FROM kg_entities
            WHERE domain = @Domain
            ORDER BY confidence DESC, updated_at DESC
            LIMIT @Limit
            """,
            new { Domain = domain, Limit = Math.Max(1, limit) },
            cancellationToken: ct));

        return rows.Select(MapEntity).ToList();
    }

    public Task<KgRelation> UpsertRelationAsync(
        string fromEntityId,
        string toEntityId,
        string relationType,
        float weight = 1.0f,
        float confidence = 0.5f,
        string[]? evidenceIds = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return UpsertRelationCoreAsync();

        async Task<KgRelation> UpsertRelationCoreAsync()
        {
            using var conn = await OpenAsync(ct);
            var existing = await conn.QuerySingleOrDefaultAsync<RelationRow>(new CommandDefinition(
                """
                SELECT *
                FROM kg_relations
                WHERE from_entity_id = @FromEntityId
                  AND to_entity_id = @ToEntityId
                  AND relation_type = @RelationType
                """,
                new
                {
                    FromEntityId = fromEntityId,
                    ToEntityId = toEntityId,
                    RelationType = relationType
                },
                cancellationToken: ct));

            if (existing is not null)
            {
                var mergedEvidence = MergeIds(DeserializeStringArray(existing.evidence_ids), evidenceIds);
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE kg_relations
                    SET weight = @Weight,
                        confidence = @Confidence,
                        evidence_ids = @EvidenceIds,
                        updated_at = @UpdatedAt
                    WHERE id = @Id
                    """,
                    new
                    {
                        Id = existing.id,
                        Weight = Math.Clamp(weight, 0f, 1f),
                        Confidence = Math.Clamp(confidence, 0f, 1f),
                        EvidenceIds = Serialize(mergedEvidence),
                        UpdatedAt = now.ToString("O")
                    },
                    cancellationToken: ct));

                await TouchEntitiesAsync(conn, new[] { fromEntityId, toEntityId }, now, ct);
                return (await GetRelationAsync(conn, existing.id, ct))!;
            }

            var relation = new KgRelation
            {
                Id = Guid.NewGuid().ToString("N"),
                FromEntityId = fromEntityId,
                ToEntityId = toEntityId,
                RelationType = relationType.Trim().ToLowerInvariant(),
                Direction = "directed",
                Weight = Math.Clamp(weight, 0f, 1f),
                Confidence = Math.Clamp(confidence, 0f, 1f),
                EvidenceIds = MergeIds(Array.Empty<string>(), evidenceIds),
                CreatedAt = now,
                UpdatedAt = now
            };

            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO kg_relations
                    (id, from_entity_id, to_entity_id, relation_type, direction, weight, confidence, evidence_ids, created_at, updated_at)
                VALUES
                    (@Id, @FromEntityId, @ToEntityId, @RelationType, @Direction, @Weight, @Confidence, @EvidenceIds, @CreatedAt, @UpdatedAt)
                """,
                new
                {
                    relation.Id,
                    relation.FromEntityId,
                    relation.ToEntityId,
                    relation.RelationType,
                    relation.Direction,
                    relation.Weight,
                    relation.Confidence,
                    EvidenceIds = Serialize(relation.EvidenceIds),
                    CreatedAt = relation.CreatedAt.ToString("O"),
                    UpdatedAt = relation.UpdatedAt.ToString("O")
                },
                cancellationToken: ct));

            await TouchEntitiesAsync(conn, new[] { fromEntityId, toEntityId }, now, ct);
            return relation;
        }
    }

    public Task<IReadOnlyList<KgRelation>> GetRelationsAsync(
        string entityId,
        string? relationType = null,
        bool incoming = false,
        CancellationToken ct = default)
    {
        return GetRelationsCoreAsync();

        async Task<IReadOnlyList<KgRelation>> GetRelationsCoreAsync()
        {
            using var conn = await OpenAsync(ct);
            var sql = incoming
                ? """
                  SELECT *
                  FROM kg_relations
                  WHERE to_entity_id = @EntityId
                    AND (@RelationType IS NULL OR relation_type = @RelationType)
                  ORDER BY confidence DESC, updated_at DESC
                  """
                : """
                  SELECT *
                  FROM kg_relations
                  WHERE from_entity_id = @EntityId
                    AND (@RelationType IS NULL OR relation_type = @RelationType)
                  ORDER BY confidence DESC, updated_at DESC
                  """;

            var rows = await conn.QueryAsync<RelationRow>(new CommandDefinition(
                sql,
                new { EntityId = entityId, RelationType = relationType },
                cancellationToken: ct));

            return rows.Select(MapRelation).ToList();
        }
    }

    public Task<GraphNeighbours> GetNeighboursAsync(
        string entityId,
        int depth = 1,
        string? relationTypeFilter = null,
        CancellationToken ct = default)
    {
        return GetNeighboursCoreAsync();

        async Task<GraphNeighbours> GetNeighboursCoreAsync()
        {
            var actualDepth = Math.Clamp(depth, 1, 4);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entityId };
            var entityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entityId };
            var relationMap = new Dictionary<string, KgRelation>(StringComparer.OrdinalIgnoreCase);
            var frontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entityId };

            using var conn = await OpenAsync(ct);
            for (var level = 0; level < actualDepth && frontier.Count > 0; level++)
            {
                var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var current in frontier)
                {
                    var relations = await GetAdjacentRelationsAsync(conn, current, relationTypeFilter, ct);
                    foreach (var relation in relations)
                    {
                        relationMap[relation.Id] = relation;

                        if (visited.Add(relation.FromEntityId))
                        {
                            nextFrontier.Add(relation.FromEntityId);
                            entityIds.Add(relation.FromEntityId);
                        }

                        if (visited.Add(relation.ToEntityId))
                        {
                            nextFrontier.Add(relation.ToEntityId);
                            entityIds.Add(relation.ToEntityId);
                        }
                    }
                }

                frontier = nextFrontier;
            }

            var entities = await GetEntitiesByIdsAsync(conn, entityIds, ct);
            return new GraphNeighbours
            {
                RootEntityId = entityId,
                Depth = actualDepth,
                Entities = entities,
                Relations = relationMap.Values.ToList()
            };
        }
    }

    public Task<GraphPath> FindPathAsync(
        string fromEntityId,
        string toEntityId,
        int maxHops = 5,
        CancellationToken ct = default)
    {
        return FindPathCoreAsync();

        async Task<GraphPath> FindPathCoreAsync()
        {
            if (string.Equals(fromEntityId, toEntityId, StringComparison.OrdinalIgnoreCase))
            {
                var entity = await GetEntityAsync(fromEntityId, ct);
                return entity is null
                    ? GraphPath.Empty
                    : new GraphPath
                    {
                        Steps = new[]
                        {
                            new GraphPathStep { Entity = entity }
                        }
                    };
            }

            var maxDepth = Math.Max(1, maxHops);
            var queue = new Queue<(string EntityId, int Depth)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fromEntityId };
            var previous = new Dictionary<string, (string FromId, KgRelation Relation)>(StringComparer.OrdinalIgnoreCase);

            queue.Enqueue((fromEntityId, 0));

            using var conn = await OpenAsync(ct);
            while (queue.Count > 0)
            {
                var (currentId, depth) = queue.Dequeue();
                if (depth >= maxDepth)
                {
                    continue;
                }

                var relations = await GetAdjacentRelationsAsync(conn, currentId, relationType: null, ct);
                foreach (var relation in relations)
                {
                    var nextId = ResolveNextEntityId(relation, currentId);
                    if (string.IsNullOrEmpty(nextId) || !visited.Add(nextId))
                    {
                        continue;
                    }

                    previous[nextId] = (currentId, relation);
                    if (string.Equals(nextId, toEntityId, StringComparison.OrdinalIgnoreCase))
                    {
                        return await BuildPathAsync(conn, fromEntityId, toEntityId, previous, ct);
                    }

                    queue.Enqueue((nextId, depth + 1));
                }
            }

            return GraphPath.Empty;
        }
    }

    public Task<IReadOnlyList<(KgEntity Entity, float Score)>> SemanticSearchAsync(
        float[] queryEmbedding,
        int limit = 10,
        CancellationToken ct = default)
    {
        return SemanticSearchCoreAsync();

        async Task<IReadOnlyList<(KgEntity Entity, float Score)>> SemanticSearchCoreAsync()
        {
            if (queryEmbedding.Length == 0)
            {
                return Array.Empty<(KgEntity Entity, float Score)>();
            }

            using var conn = await OpenAsync(ct);
            var rows = await conn.QueryAsync<EntityRow>(new CommandDefinition(
                """
                SELECT *
                FROM kg_entities
                WHERE embedding IS NOT NULL AND embedding <> ''
                """,
                cancellationToken: ct));

            return rows
                .Select(MapEntity)
                .Where(entity => entity.Embedding is { Length: > 0 })
                .Select(entity => (Entity: entity, Score: CosineSimilarity(queryEmbedding, entity.Embedding!)))
                .OrderByDescending(item => item.Score)
                .Take(Math.Max(1, limit))
                .ToList();
        }
    }

    public Task IngestMemoryAsync(
        string memoryContent,
        string[] tags,
        Func<string, Task<List<float>>> getEmbedding,
        Func<string, Task<string>> llmExtract,
        CancellationToken ct = default)
    {
        return IngestMemoryCoreAsync();

        async Task IngestMemoryCoreAsync()
        {
            if (string.IsNullOrWhiteSpace(memoryContent))
            {
                return;
            }

            try
            {
                var prompt = KgPrompts.BuildExtractionPrompt(memoryContent);
                var raw = await llmExtract(prompt);
                var payload = ParseExtractionPayload(raw);
                if (payload is null)
                {
                    _logger.LogDebug("KnowledgeGraph ingest skipped because extractor returned no parseable payload.");
                    return;
                }

                var domain = ResolveDomain(tags, payload.Entities.Select(entity => entity.Domain));
                var entityMap = new Dictionary<string, KgEntity>(StringComparer.OrdinalIgnoreCase);

                foreach (var entity in payload.Entities.Where(entity => !string.IsNullOrWhiteSpace(entity.Name)))
                {
                    ct.ThrowIfCancellationRequested();
                    var embedding = await getEmbedding($"{entity.Name}. {entity.Description ?? string.Empty}");
                    var stored = await UpsertEntityAsync(
                        entity.Name,
                        string.IsNullOrWhiteSpace(entity.Type) ? "concept" : entity.Type.Trim().ToLowerInvariant(),
                        string.IsNullOrWhiteSpace(entity.Domain) ? domain : entity.Domain.Trim().ToLowerInvariant(),
                        entity.Description,
                        aliases: null,
                        embedding: embedding.ToArray(),
                        ct: ct);

                    entityMap[entity.Name.Trim()] = stored;
                }

                foreach (var relation in payload.Relations.Where(relation =>
                             !string.IsNullOrWhiteSpace(relation.From)
                             && !string.IsNullOrWhiteSpace(relation.To)
                             && !string.IsNullOrWhiteSpace(relation.Relation)))
                {
                    ct.ThrowIfCancellationRequested();

                    if (!entityMap.TryGetValue(relation.From.Trim(), out var fromEntity))
                    {
                        var found = await FindEntityByNameAsync(relation.From.Trim(), ct);
                        if (found is not null)
                        {
                            fromEntity = found;
                            entityMap[relation.From.Trim()] = found;
                        }
                    }

                    if (!entityMap.TryGetValue(relation.To.Trim(), out var toEntity))
                    {
                        var found = await FindEntityByNameAsync(relation.To.Trim(), ct);
                        if (found is not null)
                        {
                            toEntity = found;
                            entityMap[relation.To.Trim()] = found;
                        }
                    }

                    if (fromEntity is null || toEntity is null)
                    {
                        continue;
                    }

                    await UpsertRelationAsync(
                        fromEntity.Id,
                        toEntity.Id,
                        relation.Relation.Trim().ToLowerInvariant(),
                        confidence: Math.Clamp(relation.Confidence ?? 0.5f, 0f, 1f),
                        ct: ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KnowledgeGraph ingestion failed for memory preview: {Preview}",
                    memoryContent.Length > 120 ? memoryContent[..120] : memoryContent);
            }
        }
    }

    private Task<GraphPath> BuildPathAsync(
        SqliteConnection conn,
        string fromEntityId,
        string toEntityId,
        IReadOnlyDictionary<string, (string FromId, KgRelation Relation)> previous,
        CancellationToken ct)
    {
        return BuildPathCoreAsync();

        async Task<GraphPath> BuildPathCoreAsync()
        {
            var entityIds = new List<string> { toEntityId };
            var relationByTarget = new Dictionary<string, KgRelation>(StringComparer.OrdinalIgnoreCase);
            var current = toEntityId;

            while (previous.TryGetValue(current, out var step))
            {
                relationByTarget[current] = step.Relation;
                current = step.FromId;
                entityIds.Add(current);

                if (string.Equals(current, fromEntityId, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            entityIds.Reverse();
            var entities = (await GetEntitiesByIdsAsync(conn, entityIds, ct))
                .ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);

            var steps = new List<GraphPathStep>(entityIds.Count);
            for (var i = 0; i < entityIds.Count; i++)
            {
                if (!entities.TryGetValue(entityIds[i], out var entity))
                {
                    return GraphPath.Empty;
                }

                relationByTarget.TryGetValue(entityIds[i], out var relation);
                steps.Add(new GraphPathStep
                {
                    Entity = entity,
                    ViaRelation = relation
                });
            }

            return new GraphPath { Steps = steps };
        }
    }

    private Task<KgRelation?> GetRelationAsync(SqliteConnection conn, string id, CancellationToken ct)
    {
        return GetRelationCoreAsync();

        async Task<KgRelation?> GetRelationCoreAsync()
        {
            var row = await conn.QuerySingleOrDefaultAsync<RelationRow>(new CommandDefinition(
                "SELECT * FROM kg_relations WHERE id = @Id",
                new { Id = id },
                cancellationToken: ct));

            return row is null ? null : MapRelation(row);
        }
    }

    private async Task<EntityRow?> GetEntityRowByNameAsync(SqliteConnection conn, string name, CancellationToken ct)
    {
        return await conn.QuerySingleOrDefaultAsync<EntityRow>(new CommandDefinition(
            """
            SELECT *
            FROM kg_entities
            WHERE LOWER(name) = LOWER(@Name)
            ORDER BY updated_at DESC
            LIMIT 1
            """,
            new { Name = name.Trim() },
            cancellationToken: ct));
    }

    private Task RefreshEntityConfidenceAsync(SqliteConnection conn, string entityId, CancellationToken ct)
    {
        return RefreshEntityConfidenceCoreAsync();

        async Task RefreshEntityConfidenceCoreAsync()
        {
            var aggregate = await conn.QuerySingleAsync<ConfidenceAggregate>(new CommandDefinition(
                """
                SELECT
                    COALESCE(AVG(confidence), 0.5) AS average_confidence,
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

    private Task TouchEntitiesAsync(
        SqliteConnection conn,
        IEnumerable<string> entityIds,
        DateTime timestamp,
        CancellationToken ct)
    {
        return TouchEntitiesCoreAsync();

        async Task TouchEntitiesCoreAsync()
        {
            foreach (var entityId in entityIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE kg_entities
                    SET updated_at = @UpdatedAt
                    WHERE id = @Id
                    """,
                    new { Id = entityId, UpdatedAt = timestamp.ToString("O") },
                    cancellationToken: ct));
            }
        }
    }

    private Task<IReadOnlyList<KgEntity>> GetEntitiesByIdsAsync(
        SqliteConnection conn,
        IEnumerable<string> entityIds,
        CancellationToken ct)
    {
        return GetEntitiesByIdsCoreAsync();

        async Task<IReadOnlyList<KgEntity>> GetEntitiesByIdsCoreAsync()
        {
            var ids = entityIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (ids.Length == 0)
            {
                return Array.Empty<KgEntity>();
            }

            var rows = new List<EntityRow>(ids.Length);
            foreach (var id in ids)
            {
                var row = await conn.QuerySingleOrDefaultAsync<EntityRow>(new CommandDefinition(
                    "SELECT * FROM kg_entities WHERE id = @Id",
                    new { Id = id },
                    cancellationToken: ct));

                if (row is not null)
                {
                    rows.Add(row);
                }
            }

            return rows.Select(MapEntity).ToList();
        }
    }

    private Task<IReadOnlyList<KgRelation>> GetAdjacentRelationsAsync(
        SqliteConnection conn,
        string entityId,
        string? relationType,
        CancellationToken ct)
    {
        return GetAdjacentRelationsCoreAsync();

        async Task<IReadOnlyList<KgRelation>> GetAdjacentRelationsCoreAsync()
        {
            var outgoing = await conn.QueryAsync<RelationRow>(new CommandDefinition(
                """
                SELECT *
                FROM kg_relations
                WHERE from_entity_id = @EntityId
                  AND (@RelationType IS NULL OR relation_type = @RelationType)
                """,
                new { EntityId = entityId, RelationType = relationType },
                cancellationToken: ct));

            var bidirectionalIncoming = await conn.QueryAsync<RelationRow>(new CommandDefinition(
                """
                SELECT *
                FROM kg_relations
                WHERE direction = 'bidirectional'
                  AND to_entity_id = @EntityId
                  AND (@RelationType IS NULL OR relation_type = @RelationType)
                """,
                new { EntityId = entityId, RelationType = relationType },
                cancellationToken: ct));

            return outgoing
                .Concat(bidirectionalIncoming)
                .Select(MapRelation)
                .GroupBy(relation => relation.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static KgEntity MapEntity(EntityRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        EntityType = row.entity_type,
        Domain = row.domain,
        Description = row.description,
        Aliases = DeserializeStringArray(row.aliases),
        Embedding = DeserializeFloatArray(row.embedding),
        Confidence = (float)row.confidence,
        SourceCount = row.source_count,
        CreatedAt = DateTime.Parse(row.created_at),
        UpdatedAt = DateTime.Parse(row.updated_at)
    };

    private static KgRelation MapRelation(RelationRow row) => new()
    {
        Id = row.id,
        FromEntityId = row.from_entity_id,
        ToEntityId = row.to_entity_id,
        RelationType = row.relation_type,
        Direction = row.direction,
        Weight = (float)row.weight,
        Confidence = (float)row.confidence,
        EvidenceIds = DeserializeStringArray(row.evidence_ids),
        CreatedAt = DateTime.Parse(row.created_at),
        UpdatedAt = DateTime.Parse(row.updated_at)
    };

    private static ExtractionPayload? ParseExtractionPayload(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                .Replace("```", "", StringComparison.Ordinal)
                .Trim();
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            trimmed = trimmed[firstBrace..(lastBrace + 1)];
        }

        try
        {
            return JsonSerializer.Deserialize<ExtractionPayload>(trimmed, Json);
        }
        catch
        {
            return null;
        }
    }

    private static string[] MergeAliases(IEnumerable<string> existing, IEnumerable<string>? incoming, string canonicalName)
        => existing
            .Concat(incoming ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => !value.Equals(canonicalName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] MergeIds(IEnumerable<string> existing, IEnumerable<string>? incoming)
        => existing
            .Concat(incoming ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ResolveDomain(IEnumerable<string> tags, IEnumerable<string?> extractedDomains)
    {
        var extracted = extractedDomains
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !value.Equals("general", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            return extracted.Trim().ToLowerInvariant();
        }

        var tag = tags.FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value)
            && !value.StartsWith("deep_", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("research", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("web", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(tag) ? "general" : tag.Trim().ToLowerInvariant();
    }

    private static string BuildJsonLikePattern(string value) => $"%\"{value}\"%";

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

    private static float[]? DeserializeFloatArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<float[]>(json, Json);
        }
        catch
        {
            return null;
        }
    }

    private static float ComputeTextScore(KgEntity entity, string query)
    {
        var normalizedQuery = query.Trim();
        if (entity.Name.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 3f;
        }

        if (entity.Name.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 2f;
        }

        if (entity.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1.5f;
        }

        return entity.Aliases.Any(alias => alias.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            ? 1f
            : 0f;
    }

    private static string ResolveNextEntityId(KgRelation relation, string currentId)
    {
        if (string.Equals(relation.FromEntityId, currentId, StringComparison.OrdinalIgnoreCase))
        {
            return relation.ToEntityId;
        }

        if (string.Equals(relation.ToEntityId, currentId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(relation.Direction, "bidirectional", StringComparison.OrdinalIgnoreCase))
        {
            return relation.FromEntityId;
        }

        return string.Empty;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0f;
        }

        var len = Math.Min(a.Length, b.Length);
        float dot = 0f;
        float magA = 0f;
        float magB = 0f;

        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA <= 0f || magB <= 0f)
        {
            return 0f;
        }

        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }

    private sealed class EntityRow
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string entity_type { get; set; } = "";
        public string domain { get; set; } = "general";
        public string description { get; set; } = "";
        public string aliases { get; set; } = "[]";
        public string? embedding { get; set; }
        public double confidence { get; set; }
        public int source_count { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
    }

    private sealed class RelationRow
    {
        public string id { get; set; } = "";
        public string from_entity_id { get; set; } = "";
        public string to_entity_id { get; set; } = "";
        public string relation_type { get; set; } = "";
        public string direction { get; set; } = "directed";
        public double weight { get; set; }
        public double confidence { get; set; }
        public string evidence_ids { get; set; } = "[]";
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
    }

    private sealed class ConfidenceAggregate
    {
        public double average_confidence { get; set; }
        public int source_count { get; set; }
    }

    private sealed class ExtractionPayload
    {
        public List<ExtractedEntity> Entities { get; set; } = new();
        public List<ExtractedRelation> Relations { get; set; } = new();
    }

    private sealed class ExtractedEntity
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Domain { get; set; } = "";
        public string? Description { get; set; }
    }

    private sealed class ExtractedRelation
    {
        public string From { get; set; } = "";
        public string Relation { get; set; } = "";
        public string To { get; set; } = "";
        public float? Confidence { get; set; }
    }
}
