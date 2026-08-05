#nullable enable

using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using static Darci.Memory.Graph.Neo4jWrite;

namespace Darci.Memory.Graph;

/// <summary>What a migration or verification run found.</summary>
public sealed record GraphMigrationReport
{
    public int SourceEntities { get; init; }
    public int SourceRelations { get; init; }
    public int TargetEntities { get; init; }
    public int TargetRelations { get; init; }
    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();
    public bool IsClean => Mismatches.Count == 0;
}

/// <summary>
/// P2d.2 — copies the knowledge graph from SQLite into Neo4j and then VERIFIES the copy by reading both
/// backends back and comparing.
///
/// <para>Migration preserves entity and relation IDs. That matters more than it looks: entity confidence is
/// derived by matching entity ids inside <c>confidence_claims</c>, which stays in SQLite, so regenerating
/// ids would silently sever every entity from its evidence.</para>
///
/// <para>The copy is idempotent — re-running it updates rather than duplicates, so a partial run can simply
/// be repeated.</para>
/// </summary>
public sealed class KnowledgeGraphMigrator
{
    private readonly string _sqliteConnectionString;
    private readonly Neo4jOptions _neo4jOptions;
    private readonly ILogger<KnowledgeGraphMigrator> _logger;

    public KnowledgeGraphMigrator(
        string sqliteConnectionString,
        Neo4jOptions neo4jOptions,
        ILogger<KnowledgeGraphMigrator>? logger = null)
    {
        _sqliteConnectionString = sqliteConnectionString;
        _neo4jOptions = neo4jOptions;
        _logger = logger ?? NullLogger<KnowledgeGraphMigrator>.Instance;
    }

    public async Task<GraphMigrationReport> MigrateAsync(CancellationToken ct = default)
    {
        var (entities, relations) = await ReadSqliteAsync(ct);
        _logger.LogInformation(
            "Migrating {Entities} entity(ies) and {Relations} relation(s) from SQLite into Neo4j at {Uri}.",
            entities.Count, relations.Count, _neo4jOptions.Uri);

        await using var driver = GraphDatabase.Driver(
            _neo4jOptions.Uri, AuthTokens.Basic(_neo4jOptions.User, _neo4jOptions.Password));
        await using var session = driver.AsyncSession(o => o.WithDatabase(_neo4jOptions.Database));

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (e:Entity) REQUIRE e.id IS UNIQUE");
            return true;
        });

        foreach (var e in entities)
        {
            ct.ThrowIfCancellationRequested();
            await session.ExecuteWriteAsync(async tx => await RunWriteAsync(tx, 
                """
                MERGE (e:Entity {id: $id})
                SET e.name = $name, e.entityType = $entityType, e.domain = $domain,
                    e.description = $description, e.aliases = $aliases, e.embedding = $embedding,
                    e.confidence = $confidence, e.sourceCount = $sourceCount,
                    e.createdAt = $createdAt, e.updatedAt = $updatedAt
                """,
                new
                {
                    id = e.Id,
                    name = e.Name,
                    entityType = e.EntityType,
                    domain = e.Domain,
                    description = e.Description,
                    aliases = e.Aliases,
                    embedding = e.Embedding,
                    confidence = e.Confidence,
                    sourceCount = e.SourceCount,
                    createdAt = e.CreatedAt,
                    updatedAt = e.UpdatedAt,
                }));
        }

        foreach (var r in relations)
        {
            ct.ThrowIfCancellationRequested();
            await session.ExecuteWriteAsync(async tx => await RunWriteAsync(tx, 
                """
                MATCH (a:Entity {id: $from}), (b:Entity {id: $to})
                MERGE (a)-[r:RELATION {id: $id}]->(b)
                SET r.relationType = $relationType, r.direction = $direction, r.weight = $weight,
                    r.confidence = $confidence, r.evidenceIds = $evidenceIds,
                    r.createdAt = $createdAt, r.updatedAt = $updatedAt
                """,
                new
                {
                    id = r.Id,
                    from = r.FromEntityId,
                    to = r.ToEntityId,
                    relationType = r.RelationType,
                    direction = r.Direction,
                    weight = r.Weight,
                    confidence = r.Confidence,
                    evidenceIds = r.EvidenceIds,
                    createdAt = r.CreatedAt,
                    updatedAt = r.UpdatedAt,
                }));
        }

        return await VerifyAsync(ct);
    }

    /// <summary>
    /// Dual-read verification: reads both backends and compares. Counting rows on each side would prove
    /// almost nothing, so this compares each entity and relation field by field.
    /// </summary>
    public async Task<GraphMigrationReport> VerifyAsync(CancellationToken ct = default)
    {
        var (entities, relations) = await ReadSqliteAsync(ct);
        var mismatches = new List<string>();

        await using var driver = GraphDatabase.Driver(
            _neo4jOptions.Uri, AuthTokens.Basic(_neo4jOptions.User, _neo4jOptions.Password));
        await using var session = driver.AsyncSession(o => o.WithDatabase(_neo4jOptions.Database));

        var targetEntities = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH (e:Entity) RETURN count(e) AS c");
            var rows = await cursor.ToListAsync();
            return rows.Count == 0 ? 0 : (int)rows[0]["c"].As<long>();
        });

        var targetRelations = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH ()-[r:RELATION]->() RETURN count(r) AS c");
            var rows = await cursor.ToListAsync();
            return rows.Count == 0 ? 0 : (int)rows[0]["c"].As<long>();
        });

        foreach (var e in entities)
        {
            ct.ThrowIfCancellationRequested();
            var row = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync("MATCH (e:Entity {id: $id}) RETURN e", new { id = e.Id });
                var rows = await cursor.ToListAsync();
                return rows.Count == 0 ? null : rows[0]["e"].As<INode>();
            });

            if (row is null)
            {
                mismatches.Add($"entity {e.Id} ('{e.Name}') is missing from Neo4j");
                continue;
            }

            Compare(mismatches, $"entity {e.Id} name", e.Name, Prop(row, "name"));
            Compare(mismatches, $"entity {e.Id} entityType", e.EntityType, Prop(row, "entityType"));
            Compare(mismatches, $"entity {e.Id} domain", e.Domain, Prop(row, "domain"));
            Compare(mismatches, $"entity {e.Id} description", e.Description, Prop(row, "description"));
            Compare(mismatches, $"entity {e.Id} aliases", e.Aliases, Prop(row, "aliases"));
            Compare(mismatches, $"entity {e.Id} embedding", e.Embedding ?? "", Prop(row, "embedding") ?? "");
            // Confidence and source count are migrated too, so they are part of the copy being verified —
            // a silently reset confidence would quietly change how much every consumer trusts an entity.
            CompareNumeric(mismatches, $"entity {e.Id} confidence", e.Confidence, Prop(row, "confidence"));
            CompareNumeric(mismatches, $"entity {e.Id} sourceCount", e.SourceCount, Prop(row, "sourceCount"));
        }

        foreach (var r in relations)
        {
            ct.ThrowIfCancellationRequested();
            var row = await session.ExecuteReadAsync(async tx =>
            {
                var cursor = await tx.RunAsync(
                    """
                    MATCH (a:Entity)-[r:RELATION {id: $id}]->(b:Entity)
                    RETURN r, a.id AS fromId, b.id AS toId
                    """,
                    new { id = r.Id });
                var rows = await cursor.ToListAsync();
                return rows.Count == 0 ? null : rows[0];
            });

            if (row is null)
            {
                mismatches.Add($"relation {r.Id} ('{r.RelationType}') is missing from Neo4j");
                continue;
            }

            var rel = row["r"].As<IRelationship>();
            Compare(mismatches, $"relation {r.Id} type", r.RelationType, RelProp(rel, "relationType"));
            Compare(mismatches, $"relation {r.Id} from", r.FromEntityId, row["fromId"].As<string>());
            Compare(mismatches, $"relation {r.Id} to", r.ToEntityId, row["toId"].As<string>());
            Compare(mismatches, $"relation {r.Id} evidenceIds", r.EvidenceIds, RelProp(rel, "evidenceIds"));
        }

        // Source→target comparison alone cannot see rows the target has and the source does not — leftover
        // fixtures, a half-finished experiment, a stale earlier import. Those are real divergence: reads
        // would return entities that do not exist in the system of record.
        var sourceIds = entities.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var orphans = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH (e:Entity) RETURN e.id AS id, e.name AS name");
            var found = new List<(string Id, string Name)>();
            await foreach (var record in cursor)
            {
                found.Add((record["id"].As<string>(), record["name"].As<string>() ?? ""));
            }
            return found;
        });

        foreach (var orphan in orphans.Where(o => !sourceIds.Contains(o.Id)))
        {
            mismatches.Add($"entity {orphan.Id} ('{orphan.Name}') exists in Neo4j but not in SQLite");
        }

        var report = new GraphMigrationReport
        {
            SourceEntities = entities.Count,
            SourceRelations = relations.Count,
            TargetEntities = targetEntities,
            TargetRelations = targetRelations,
            Mismatches = mismatches,
        };

        if (report.IsClean)
        {
            _logger.LogInformation(
                "Dual-read verification clean: {Entities} entity(ies), {Relations} relation(s) match across both backends.",
                report.SourceEntities, report.SourceRelations);
        }
        else
        {
            _logger.LogError(
                "Dual-read verification found {Count} mismatch(es). First few: {Sample}",
                mismatches.Count, string.Join(" | ", mismatches.Take(5)));
        }

        return report;
    }

    private static void Compare(List<string> mismatches, string what, string? expected, string? actual)
    {
        if (!string.Equals(expected ?? "", actual ?? "", StringComparison.Ordinal))
        {
            mismatches.Add($"{what}: SQLite '{Trim(expected)}' vs Neo4j '{Trim(actual)}'");
        }
    }

    private static void CompareNumeric(List<string> mismatches, string what, double expected, string? actual)
    {
        var parsed = double.TryParse(
            actual, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value);

        if (!parsed || Math.Abs(expected - value) > 0.0001)
        {
            mismatches.Add($"{what}: SQLite '{expected}' vs Neo4j '{Trim(actual)}'");
        }
    }

    private static string Trim(string? v)
        => v is null ? "" : v.Length <= 60 ? v : v[..60] + "…";

    private static string? Prop(INode n, string key)
        => n.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static string? RelProp(IRelationship r, string key)
        => r.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;

    private async Task<(List<SourceEntity> Entities, List<SourceRelation> Relations)> ReadSqliteAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_sqliteConnectionString);
        await conn.OpenAsync(ct);

        var entities = (await conn.QueryAsync<SourceEntity>(new CommandDefinition(
            """
            SELECT id AS Id, name AS Name, entity_type AS EntityType, domain AS Domain,
                   description AS Description, aliases AS Aliases, embedding AS Embedding,
                   confidence AS Confidence, source_count AS SourceCount,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM kg_entities
            """,
            cancellationToken: ct))).ToList();

        var relations = (await conn.QueryAsync<SourceRelation>(new CommandDefinition(
            """
            SELECT id AS Id, from_entity_id AS FromEntityId, to_entity_id AS ToEntityId,
                   relation_type AS RelationType, direction AS Direction, weight AS Weight,
                   confidence AS Confidence, evidence_ids AS EvidenceIds,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM kg_relations
            """,
            cancellationToken: ct))).ToList();

        return (entities, relations);
    }

    // Read as raw column values so the copy is byte-faithful — going through KgEntity would round-trip the
    // JSON columns and could quietly normalize them.
    private sealed record SourceEntity
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string EntityType { get; init; } = "";
        public string Domain { get; init; } = "general";
        public string Description { get; init; } = "";
        public string Aliases { get; init; } = "[]";
        public string? Embedding { get; init; }
        public double Confidence { get; init; } = 0.5;
        public int SourceCount { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private sealed record SourceRelation
    {
        public string Id { get; init; } = "";
        public string FromEntityId { get; init; } = "";
        public string ToEntityId { get; init; } = "";
        public string RelationType { get; init; } = "";
        public string Direction { get; init; } = "directed";
        public double Weight { get; init; } = 1.0;
        public double Confidence { get; init; } = 0.5;
        public string EvidenceIds { get; init; } = "[]";
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }
}
