#nullable enable

using System.Text.Json;
using Dapper;
using Darci.Memory.Graph.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using static Darci.Memory.Graph.Neo4jWrite;

namespace Darci.Memory.Graph;

/// <summary>Connection settings for the Neo4j-backed knowledge graph. Values come from .env.local.</summary>
public sealed record Neo4jOptions
{
    public string Uri { get; init; } = "bolt://localhost:7687";
    public string User { get; init; } = "neo4j";
    public string Password { get; init; } = "";
    public string Database { get; init; } = "neo4j";

    /// <summary>
    /// The SQLite database that owns <c>confidence_claims</c>. Entity confidence is DERIVED from claims, and
    /// ConfidenceTracker stays on SQLite by decision — so the Neo4j graph reads the aggregate from there to
    /// keep entity confidence behaving exactly as it does today. See the note on
    /// <see cref="Neo4jKnowledgeGraph"/>.
    /// </summary>
    public string? ClaimsConnectionString { get; init; }

    public static Neo4jOptions FromEnvironment() => new()
    {
        Uri = Env("DARCI_NEO4J_URI") ?? "bolt://localhost:7687",
        User = Env("DARCI_NEO4J_USER") ?? "neo4j",
        Password = Env("DARCI_NEO4J_PASSWORD") ?? "",
        Database = Env("DARCI_NEO4J_DATABASE") ?? "neo4j",
    };

    /// <summary>True when the host has been configured to use Neo4j at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Password);

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}

/// <summary>
/// Neo4j-backed <see cref="IKnowledgeGraph"/> (Phase 2 P2d). ONLY the backing store changes — the interface
/// is fixed, so <see cref="IMemoryBroker"/> and every node above it are untouched.
///
/// <para><b>Data model.</b> <c>kg_entities</c> → <c>(:Entity {id, name, …})</c>;
/// <c>kg_relations</c> → <c>(:Entity)-[:RELATION {relationType, …}]->(:Entity)</c>. A single relationship
/// type with a <c>relationType</c> property (rather than a dynamic Cypher type) because Neo4j 5.26 cannot
/// parameterize relationship types without APOC, and it maps 1:1 onto the relational model this replaces.</para>
///
/// <para><b>Semantic search: in-app cosine</b>, matching the SQLite implementation exactly — embeddings are
/// loaded and scored in process. TODO (future optimization, deliberately NOT part of a no-behavior-change
/// swap): Neo4j 5 native vector indexes would push this into the database and scale far better, but they
/// require a fixed embedding dimension and index management, and the win only matters at a KG size well
/// beyond today's.</para>
///
/// <para><b>Cross-store read, called out honestly.</b> Entity confidence is derived from
/// <c>confidence_claims</c>, which belongs to ConfidenceTracker and stays on SQLite by decision. So this
/// class reads that aggregate from SQLite on upsert, exactly as the SQLite graph did. The alternative —
/// dropping the refresh — would silently freeze every entity's confidence at 0.5, which is a behavior change
/// this swap must not make.</para>
/// </summary>
public sealed class Neo4jKnowledgeGraph : IKnowledgeGraph, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly IDriver _driver;
    private readonly Neo4jOptions _options;
    private readonly ILogger<Neo4jKnowledgeGraph> _logger;

    public Neo4jKnowledgeGraph(Neo4jOptions options, ILogger<Neo4jKnowledgeGraph>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<Neo4jKnowledgeGraph>.Instance;
        _driver = GraphDatabase.Driver(options.Uri, AuthTokens.Basic(options.User, options.Password));
    }

    private IAsyncSession Session() => _driver.AsyncSession(o => o.WithDatabase(_options.Database));

    /// <summary>
    /// Cheap, BOUNDED reachability check used to decide the backing store at startup.
    ///
    /// <para>Configuration presence is not the same question as reachability. Credentials sitting in
    /// .env.local say a host WANTS Neo4j, not that Neo4j is running — and the driver's own retry policy
    /// spends 30 seconds discovering the difference before throwing, which is long enough to look like a
    /// hang and fatal enough to take the process down. This answers in <paramref name="timeout"/> and
    /// never throws, so the caller can fall back instead of crashing.</para>
    /// </summary>
    public static async Task<(bool Reachable, string Reason)> ProbeAsync(
        Neo4jOptions options, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            await using var driver = GraphDatabase.Driver(
                options.Uri, AuthTokens.Basic(options.User, options.Password));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            await driver.VerifyConnectivityAsync().WaitAsync(cts.Token);
            return (true, "connected");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"no response within {timeout.TotalSeconds:0.#}s");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var session = Session();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (e:Entity) REQUIRE e.id IS UNIQUE");
            await tx.RunAsync("CREATE INDEX entity_name IF NOT EXISTS FOR (e:Entity) ON (e.name)");
            await tx.RunAsync("CREATE INDEX entity_domain IF NOT EXISTS FOR (e:Entity) ON (e.domain)");
            await tx.RunAsync("CREATE INDEX relation_id IF NOT EXISTS FOR ()-[r:RELATION]-() ON (r.id)");
            return true;
        });

        _logger.LogInformation("Neo4j knowledge graph initialized at {Uri} (database '{Db}').", _options.Uri, _options.Database);
    }

    // ── entities ──

    public async Task<KgEntity> UpsertEntityAsync(
        string name, string entityType, string domain, string? description = null,
        string[]? aliases = null, float[]? embedding = null, CancellationToken ct = default)
    {
        var normalizedName = name.Trim();
        var now = DateTime.UtcNow;

        var existing = await FindEntityByNameAsync(normalizedName, ct);

        string id;
        if (existing is not null)
        {
            id = existing.Id;
            var mergedAliases = MergeAliases(existing.Aliases, aliases, normalizedName);
            await using var session = Session();
            await session.ExecuteWriteAsync(async tx => await RunWriteAsync(tx, 
                """
                MATCH (e:Entity {id: $id})
                SET e.entityType = $entityType,
                    e.domain = $domain,
                    e.description = $description,
                    e.aliases = $aliases,
                    e.embedding = $embedding,
                    e.updatedAt = $updatedAt
                """,
                new
                {
                    id,
                    entityType = string.IsNullOrWhiteSpace(entityType) ? existing.EntityType : entityType,
                    domain = string.IsNullOrWhiteSpace(domain) ? existing.Domain : domain,
                    description = string.IsNullOrWhiteSpace(description) ? existing.Description : description,
                    aliases = Serialize(mergedAliases),
                    embedding = embedding is null ? (existing.Embedding is null ? null : Serialize(existing.Embedding)) : Serialize(embedding),
                    updatedAt = Iso(now),
                }));
        }
        else
        {
            id = Guid.NewGuid().ToString("N");
            await using var session = Session();
            await session.ExecuteWriteAsync(async tx => await RunWriteAsync(tx, 
                """
                CREATE (e:Entity {
                    id: $id, name: $name, entityType: $entityType, domain: $domain, description: $description,
                    aliases: $aliases, embedding: $embedding, confidence: $confidence, sourceCount: $sourceCount,
                    createdAt: $createdAt, updatedAt: $updatedAt
                })
                """,
                new
                {
                    id,
                    name = normalizedName,
                    entityType,
                    domain = string.IsNullOrWhiteSpace(domain) ? "general" : domain,
                    description = description ?? "",
                    aliases = Serialize(MergeAliases(Array.Empty<string>(), aliases, normalizedName)),
                    embedding = embedding is null ? null : Serialize(embedding),
                    confidence = 0.5,
                    sourceCount = 0,
                    createdAt = Iso(now),
                    updatedAt = Iso(now),
                }));
        }

        await RefreshEntityConfidenceAsync(id, ct);
        return (await GetEntityAsync(id, ct))!;
    }

    public async Task<KgEntity?> GetEntityAsync(string id, CancellationToken ct = default)
    {
        await using var session = Session();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH (e:Entity {id: $id}) RETURN e", new { id });
            var records = await cursor.ToListAsync();
            return records.Count == 0 ? null : MapEntity(records[0]["e"].As<INode>());
        });
    }

    public async Task<KgEntity?> FindEntityByNameAsync(string name, CancellationToken ct = default)
    {
        var normalized = name.Trim();
        await using var session = Session();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (e:Entity) WHERE toLower(e.name) = toLower($name) RETURN e LIMIT 1",
                new { name = normalized });
            var records = await cursor.ToListAsync();
            return records.Count == 0 ? null : MapEntity(records[0]["e"].As<INode>());
        });
    }

    public async Task<IReadOnlyList<KgEntity>> SearchEntitiesAsync(
        string query, string? domain = null, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<KgEntity>();

        var trimmed = query.Trim();
        await using var session = Session();
        var entities = await session.ExecuteReadAsync(async tx =>
        {
            // Mirrors the SQLite `LOWER(name) LIKE %q% OR LOWER(aliases) LIKE %q%` with optional domain filter.
            var cursor = await tx.RunAsync(
                """
                MATCH (e:Entity)
                WHERE ($domain IS NULL OR e.domain = $domain)
                  AND (toLower(e.name) CONTAINS toLower($q) OR toLower(coalesce(e.aliases, '')) CONTAINS toLower($q))
                RETURN e
                ORDER BY e.updatedAt DESC
                LIMIT $limit
                """,
                new { domain = string.IsNullOrWhiteSpace(domain) ? null : domain, q = trimmed, limit = Math.Max(1, limit) });

            var list = new List<KgEntity>();
            await foreach (var record in cursor) list.Add(MapEntity(record["e"].As<INode>()));
            return list;
        });

        // Same ranking as the SQLite implementation: text score, then recency.
        return entities
            .OrderByDescending(e => ComputeTextScore(e, trimmed))
            .ThenByDescending(e => e.UpdatedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<KgEntity>> GetEntitiesByDomainAsync(
        string domain, int limit = 100, CancellationToken ct = default)
    {
        await using var session = Session();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (e:Entity {domain: $domain}) RETURN e ORDER BY e.updatedAt DESC LIMIT $limit",
                new { domain, limit = Math.Max(1, limit) });
            var list = new List<KgEntity>();
            await foreach (var record in cursor) list.Add(MapEntity(record["e"].As<INode>()));
            return (IReadOnlyList<KgEntity>)list;
        });
    }

    // ── relations ──

    public async Task<KgRelation> UpsertRelationAsync(
        string fromEntityId, string toEntityId, string relationType,
        float weight = 1.0f, float confidence = 0.5f, string[]? evidenceIds = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var session = Session();

        return await session.ExecuteWriteAsync(async tx =>
        {
            // Idempotent on (from, to, relationType), like the SQLite unique key.
            var existing = await tx.RunAsync(
                """
                MATCH (a:Entity {id: $from})-[r:RELATION {relationType: $type}]->(b:Entity {id: $to})
                RETURN r LIMIT 1
                """,
                new { from = fromEntityId, to = toEntityId, type = relationType });
            var found = await existing.ToListAsync();

            if (found.Count > 0)
            {
                var current = MapRelation(found[0]["r"].As<IRelationship>(), fromEntityId, toEntityId);
                var merged = MergeIds(current.EvidenceIds, evidenceIds);
                var updated = await tx.RunAsync(
                    """
                    MATCH (a:Entity {id: $from})-[r:RELATION {relationType: $type}]->(b:Entity {id: $to})
                    SET r.weight = $weight, r.confidence = $confidence, r.evidenceIds = $evidence, r.updatedAt = $updatedAt
                    RETURN r
                    """,
                    new
                    {
                        from = fromEntityId, to = toEntityId, type = relationType,
                        weight = (double)weight, confidence = (double)confidence,
                        evidence = Serialize(merged), updatedAt = Iso(now),
                    });
                var rows = await updated.ToListAsync();
                return MapRelation(rows[0]["r"].As<IRelationship>(), fromEntityId, toEntityId);
            }

            var created = await tx.RunAsync(
                """
                MATCH (a:Entity {id: $from}), (b:Entity {id: $to})
                CREATE (a)-[r:RELATION {
                    id: $id, relationType: $type, direction: 'directed', weight: $weight,
                    confidence: $confidence, evidenceIds: $evidence, createdAt: $createdAt, updatedAt: $updatedAt
                }]->(b)
                RETURN r
                """,
                new
                {
                    from = fromEntityId, to = toEntityId, id = Guid.NewGuid().ToString("N"), type = relationType,
                    weight = (double)weight, confidence = (double)confidence,
                    evidence = Serialize(MergeIds(Array.Empty<string>(), evidenceIds)),
                    createdAt = Iso(now), updatedAt = Iso(now),
                });
            var createdRows = await created.ToListAsync();
            return MapRelation(createdRows[0]["r"].As<IRelationship>(), fromEntityId, toEntityId);
        });
    }

    public async Task<IReadOnlyList<KgRelation>> GetRelationsAsync(
        string entityId, string? relationType = null, bool incoming = false, CancellationToken ct = default)
    {
        await using var session = Session();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cypher = incoming
                ? """
                  MATCH (a:Entity)-[r:RELATION]->(b:Entity {id: $id})
                  WHERE $type IS NULL OR r.relationType = $type
                  RETURN r, a.id AS fromId, b.id AS toId ORDER BY r.updatedAt DESC
                  """
                : """
                  MATCH (a:Entity {id: $id})-[r:RELATION]->(b:Entity)
                  WHERE $type IS NULL OR r.relationType = $type
                  RETURN r, a.id AS fromId, b.id AS toId ORDER BY r.updatedAt DESC
                  """;

            var cursor = await tx.RunAsync(cypher, new { id = entityId, type = relationType });
            var list = new List<KgRelation>();
            await foreach (var record in cursor)
                list.Add(MapRelation(record["r"].As<IRelationship>(), record["fromId"].As<string>(), record["toId"].As<string>()));
            return (IReadOnlyList<KgRelation>)list;
        });
    }

    public async Task<GraphNeighbours> GetNeighboursAsync(
        string entityId, int depth = 1, string? relationTypeFilter = null, CancellationToken ct = default)
    {
        var safeDepth = Math.Clamp(depth, 1, 5);
        await using var session = Session();

        return await session.ExecuteReadAsync(async tx =>
        {
            // Variable-length traversal in either direction, which is what the SQLite version emulated with
            // an iterative frontier expansion.
            var cursor = await tx.RunAsync(
                $$"""
                  MATCH path = (root:Entity {id: $id})-[rels:RELATION*1..{{safeDepth}}]-(other:Entity)
                  WHERE $type IS NULL OR ALL(r IN rels WHERE r.relationType = $type)
                  UNWIND rels AS r
                  WITH collect(DISTINCT other) AS others, collect(DISTINCT r) AS relations
                  RETURN others, relations
                  """,
                new { id = entityId, type = relationTypeFilter });

            var records = await cursor.ToListAsync();
            var entities = new List<KgEntity>();
            var relations = new List<KgRelation>();

            // The SQLite implementation seeds its frontier WITH the root and returns it among the entities,
            // so the root belongs in the result even though Cypher's `(other)` pattern excludes it.
            var rootCursor = await tx.RunAsync("MATCH (e:Entity {id: $id}) RETURN e", new { id = entityId });
            var rootRecords = await rootCursor.ToListAsync();
            if (rootRecords.Count > 0)
            {
                entities.Add(MapEntity(rootRecords[0]["e"].As<INode>()));
            }

            if (records.Count > 0)
            {
                foreach (var node in records[0]["others"].As<List<object>>())
                {
                    var mapped = MapEntity(node.As<INode>());
                    // A cycle back to the root would otherwise list it twice.
                    if (entities.All(e => e.Id != mapped.Id)) entities.Add(mapped);
                }

                // Endpoint ids are resolved per relationship so the mapped relation matches the SQLite shape.
                foreach (var rel in records[0]["relations"].As<List<object>>())
                {
                    var r = rel.As<IRelationship>();
                    var ends = await ResolveEndpointsAsync(tx, r);
                    relations.Add(MapRelation(r, ends.From, ends.To));
                }
            }

            return new GraphNeighbours
            {
                RootEntityId = entityId,
                Depth = safeDepth,
                Entities = entities,
                Relations = relations,
            };
        });
    }

    public async Task<GraphPath> FindPathAsync(
        string fromEntityId, string toEntityId, int maxHops = 5, CancellationToken ct = default)
    {
        var hops = Math.Clamp(maxHops, 1, 10);
        await using var session = Session();

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $$"""
                  MATCH path = shortestPath((a:Entity {id: $from})-[:RELATION*1..{{hops}}]-(b:Entity {id: $to}))
                  RETURN nodes(path) AS ns, relationships(path) AS rs
                  """,
                new { from = fromEntityId, to = toEntityId });

            var records = await cursor.ToListAsync();
            if (records.Count == 0) return GraphPath.Empty;

            var nodes = records[0]["ns"].As<List<object>>().Select(n => MapEntity(n.As<INode>())).ToList();
            var rels = records[0]["rs"].As<List<object>>().Select(r => r.As<IRelationship>()).ToList();

            var steps = new List<GraphPathStep>();
            for (var i = 0; i < nodes.Count; i++)
            {
                KgRelation? via = null;
                if (i > 0 && i - 1 < rels.Count)
                {
                    var ends = await ResolveEndpointsAsync(tx, rels[i - 1]);
                    via = MapRelation(rels[i - 1], ends.From, ends.To);
                }
                steps.Add(new GraphPathStep { Entity = nodes[i], ViaRelation = via });
            }

            return new GraphPath { Steps = steps };
        });
    }

    // ── semantic search (in-app cosine, matching SQLite exactly) ──

    public async Task<IReadOnlyList<(KgEntity Entity, float Score)>> SemanticSearchAsync(
        float[] queryEmbedding, int limit = 10, CancellationToken ct = default)
    {
        if (queryEmbedding.Length == 0) return Array.Empty<(KgEntity, float)>();

        await using var session = Session();
        var candidates = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (e:Entity) WHERE e.embedding IS NOT NULL AND e.embedding <> '' RETURN e");
            var list = new List<KgEntity>();
            await foreach (var record in cursor) list.Add(MapEntity(record["e"].As<INode>()));
            return list;
        });

        return candidates
            .Where(e => e.Embedding is { Length: > 0 })
            .Select(e => (Entity: e, Score: CosineSimilarity(queryEmbedding, e.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    // ── ingestion ──

    public async Task IngestMemoryAsync(
        string memoryContent, string[] tags, Func<string, Task<List<float>>> getEmbedding,
        Func<string, Task<string>> llmExtract, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memoryContent)) return;

        try
        {
            var prompt = BuildExtractionPrompt(memoryContent);
            var raw = await llmExtract(prompt);
            var extraction = ParseExtraction(raw);
            if (extraction is null) return;

            var domain = ResolveDomain(tags, extraction.Entities.Select(e => e.Domain));
            var byName = new Dictionary<string, KgEntity>(StringComparer.OrdinalIgnoreCase);

            foreach (var extracted in extraction.Entities)
            {
                if (string.IsNullOrWhiteSpace(extracted.Name)) continue;

                float[]? embedding = null;
                try
                {
                    var vector = await getEmbedding($"{extracted.Name}. {extracted.Description ?? string.Empty}");
                    embedding = vector.ToArray();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Embedding failed while ingesting '{Name}' (non-fatal).", extracted.Name);
                }

                var entity = await UpsertEntityAsync(
                    extracted.Name, string.IsNullOrWhiteSpace(extracted.Type) ? "Concept" : extracted.Type!,
                    string.IsNullOrWhiteSpace(extracted.Domain) ? domain : extracted.Domain!,
                    extracted.Description, extracted.Aliases, embedding, ct);

                byName[entity.Name] = entity;
                foreach (var alias in entity.Aliases) byName[alias] = entity;
            }

            foreach (var relation in extraction.Relations)
            {
                if (string.IsNullOrWhiteSpace(relation.From) || string.IsNullOrWhiteSpace(relation.To)) continue;
                if (!byName.TryGetValue(relation.From!, out var from) || !byName.TryGetValue(relation.To!, out var to)) continue;

                await UpsertRelationAsync(
                    from.Id, to.Id,
                    string.IsNullOrWhiteSpace(relation.Type) ? "related_to" : relation.Type!,
                    ct: ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Memory ingestion into Neo4j failed (non-fatal).");
        }
    }

    // ── confidence (cross-store: claims live in SQLite by decision) ──

    private async Task RefreshEntityConfidenceAsync(string entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ClaimsConnectionString)) return;

        double average = 0.5;
        long sourceCount = 0;
        try
        {
            await using var conn = new SqliteConnection(_options.ClaimsConnectionString);
            await conn.OpenAsync(ct);

            // The claims table belongs to ConfidenceTracker; if it is absent this is simply a no-op.
            var exists = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='confidence_claims'",
                cancellationToken: ct));
            if (exists == 0) return;

            var row = await conn.QuerySingleAsync<(double avg, long count)>(new CommandDefinition(
                """
                SELECT COALESCE(AVG(confidence), 0.5) AS avg, COUNT(*) AS count
                FROM confidence_claims
                WHERE entity_ids LIKE @Pattern
                """,
                new { Pattern = $"%\"{entityId}\"%" },
                cancellationToken: ct));
            average = row.avg;
            sourceCount = row.count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Confidence refresh from the claims store failed (non-fatal).");
            return;
        }

        await using var session = Session();
        await session.ExecuteWriteAsync(async tx => await RunWriteAsync(tx, 
            "MATCH (e:Entity {id: $id}) SET e.confidence = $confidence, e.sourceCount = $sourceCount, e.updatedAt = $updatedAt",
            new
            {
                id = entityId,
                confidence = Math.Clamp(average, 0d, 1d),
                sourceCount,
                updatedAt = Iso(DateTime.UtcNow),
            }));
    }

    // ── mapping + helpers (semantics copied from the SQLite implementation) ──

    private static async Task<(string From, string To)> ResolveEndpointsAsync(IAsyncQueryRunner tx, IRelationship rel)
    {
        var cursor = await tx.RunAsync(
            "MATCH (a)-[r:RELATION]->(b) WHERE elementId(r) = $eid RETURN a.id AS fromId, b.id AS toId",
            new { eid = rel.ElementId });
        var rows = await cursor.ToListAsync();
        return rows.Count == 0 ? ("", "") : (rows[0]["fromId"].As<string>(), rows[0]["toId"].As<string>());
    }

    private static KgEntity MapEntity(INode node) => new()
    {
        Id = Get(node, "id"),
        Name = Get(node, "name"),
        EntityType = Get(node, "entityType"),
        Domain = string.IsNullOrWhiteSpace(Get(node, "domain")) ? "general" : Get(node, "domain"),
        Description = Get(node, "description"),
        Aliases = DeserializeStringArray(GetOrNull(node, "aliases")),
        Embedding = DeserializeFloatArray(GetOrNull(node, "embedding")),
        Confidence = (float)GetDouble(node, "confidence", 0.5),
        SourceCount = (int)GetLong(node, "sourceCount"),
        CreatedAt = ParseIso(Get(node, "createdAt")),
        UpdatedAt = ParseIso(Get(node, "updatedAt")),
    };

    private static KgRelation MapRelation(IRelationship rel, string fromId, string toId) => new()
    {
        Id = RelGet(rel, "id"),
        FromEntityId = fromId,
        ToEntityId = toId,
        RelationType = RelGet(rel, "relationType"),
        Direction = string.IsNullOrWhiteSpace(RelGet(rel, "direction")) ? "directed" : RelGet(rel, "direction"),
        Weight = (float)RelGetDouble(rel, "weight", 1.0),
        Confidence = (float)RelGetDouble(rel, "confidence", 0.5),
        EvidenceIds = DeserializeStringArray(RelGetOrNull(rel, "evidenceIds")),
        CreatedAt = ParseIso(RelGet(rel, "createdAt")),
        UpdatedAt = ParseIso(RelGet(rel, "updatedAt")),
    };

    private static string Get(INode n, string key) => n.Properties.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";
    private static string? GetOrNull(INode n, string key) => n.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;
    private static double GetDouble(INode n, string key, double fallback)
        => n.Properties.TryGetValue(key, out var v) && v is not null && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
    private static long GetLong(INode n, string key)
        => n.Properties.TryGetValue(key, out var v) && v is not null && long.TryParse(v.ToString(), out var l) ? l : 0;

    private static string RelGet(IRelationship r, string key) => r.Properties.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";
    private static string? RelGetOrNull(IRelationship r, string key) => r.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;
    private static double RelGetDouble(IRelationship r, string key, double fallback)
        => r.Properties.TryGetValue(key, out var v) && v is not null && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private static string Iso(DateTime v) => v.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime ParseIso(string? v) =>
        DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.MinValue;

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static string[] DeserializeStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json, Json) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static float[]? DeserializeFloatArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<float[]>(json, Json); }
        catch { return null; }
    }

    private static string[] MergeAliases(IEnumerable<string> existing, IEnumerable<string>? incoming, string canonicalName)
        => existing
            .Concat(incoming ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Where(v => !v.Equals(canonicalName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] MergeIds(IEnumerable<string> existing, IEnumerable<string>? incoming)
        => existing
            .Concat(incoming ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ResolveDomain(IEnumerable<string> tags, IEnumerable<string?> extractedDomains)
    {
        var extracted = extractedDomains.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !v.Equals("general", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(extracted)) return extracted.Trim().ToLowerInvariant();

        var tag = tags.FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(v)
            && !v.StartsWith("deep_", StringComparison.OrdinalIgnoreCase)
            && !v.Equals("research", StringComparison.OrdinalIgnoreCase)
            && !v.Equals("web", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(tag) ? "general" : tag.Trim().ToLowerInvariant();
    }

    internal static float ComputeTextScore(KgEntity entity, string query)
    {
        var q = query.Trim();
        if (entity.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) return 3f;
        if (entity.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2f;
        if (entity.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))) return 1f;
        return 0f;
    }

    internal static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0f;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 0f;
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    private static string BuildExtractionPrompt(string content) => $$"""
        Extract entities and relations from the text below. Respond with ONLY JSON:
        {"entities":[{"name":"...","type":"...","domain":"...","description":"...","aliases":["..."]}],
         "relations":[{"from":"...","to":"...","type":"..."}]}

        TEXT:
        {{content}}
        JSON:
        """;

    private static Extraction? ParseExtraction(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<Extraction>(raw[start..(end + 1)], new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return null; }
    }

    private sealed class Extraction
    {
        public List<ExtractedEntity> Entities { get; set; } = new();
        public List<ExtractedRelation> Relations { get; set; } = new();
    }

    private sealed class ExtractedEntity
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Domain { get; set; }
        public string? Description { get; set; }
        public string[]? Aliases { get; set; }
    }

    private sealed class ExtractedRelation
    {
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Type { get; set; }
    }

    public async ValueTask DisposeAsync() => await _driver.DisposeAsync();
}
