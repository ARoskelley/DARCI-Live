#nullable enable

using Darci.Memory.Graph;
using Darci.Memory.Graph.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

public sealed class CodingContextBuilder : ICodingContextBuilder
{
    private const int MaxPreviewBytes = 200_000;
    private const int MaxPreviewChars = 4_000;

    /// <summary>Runs under the coding node; reads the graph only (its manifest declares read:knowledge).</summary>
    private static readonly MemoryAccess Access =
        MemoryAccess.ForNode("darci.coding", new[] { MemoryScopes.ReadKnowledge });

    private readonly ICodingWorkspaceStore _store;
    private readonly IModelRouter _router;
    private readonly IMemoryBroker _memory;
    private readonly ILogger<CodingContextBuilder> _logger;

    public CodingContextBuilder(
        ICodingWorkspaceStore store,
        IModelRouter router,
        IMemoryBroker memory,
        ILogger<CodingContextBuilder> logger)
    {
        _store = store;
        _router = router;
        _memory = memory;
        _logger = logger;
    }

    public async Task<CodingContextPackage> BuildAsync(
        string workspaceId, string? query = null, int limit = 8, CancellationToken ct = default)
    {
        var workspace = await _store.GetWorkspaceAsync(workspaceId, ct)
            ?? throw new InvalidOperationException($"Coding workspace not found: {workspaceId}");

        var files = await _store.GetFilesAsync(workspaceId, 10_000, ct);
        var tokens = Tokenize(query);

        // Score files using the deterministic heuristic.
        var scored = files
            .Where(f => f.IsText)
            .Select(f => (File: f, Score: ScoreFile(f, tokens)))
            .ToList();

        // If there is a query, attempt to re-rank with embedding cosine similarity.
        if (!string.IsNullOrWhiteSpace(query))
        {
            scored = await ReRankWithEmbeddingsAsync(workspaceId, query, scored, ct);
        }

        // Fetch KG hits early so they can boost file scores before selection.
        var kgHits = await FetchKgHitsAsync(query, ct);

        // Boost scores of files that the KG says contain matched symbols.
        if (kgHits.Count > 0)
        {
            scored = await ApplyKgFileBoostAsync(scored, kgHits, ct);
        }

        var selected = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 25))
            .ToList();

        var contextFiles = new List<CodingContextFile>();
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            contextFiles.Add(new CodingContextFile
            {
                RelativePath = item.File.RelativePath,
                Kind = item.File.Kind,
                SizeBytes = item.File.SizeBytes,
                RelevanceScore = item.Score,
                Preview = await ReadPreviewAsync(workspace.RootPath, item.File, ct)
            });
        }

        var notes = new List<string> { workspace.Summary };
        if (kgHits.Count > 0)
        {
            notes.Add($"Found {kgHits.Count} related KG symbol(s) for this query.");
        }

        if (contextFiles.Count == 0)
        {
            notes.Add("No matching text files were selected. Try a more specific query or re-import the workspace.");
        }

        _logger.LogDebug("Built context package for {WorkspaceId}: {Files} files, {KgHits} KG hits.",
            workspaceId, contextFiles.Count, kgHits.Count);

        return new CodingContextPackage
        {
            WorkspaceId = workspace.Id,
            Query = query ?? "",
            GeneratedAt = DateTime.UtcNow,
            SuggestedCommands = workspace.DetectedCommands,
            Notes = notes.ToArray(),
            Files = contextFiles,
            KgHits = kgHits
        };
    }

    private async Task<List<(CodingFileEntry File, float Score)>> ReRankWithEmbeddingsAsync(
        string workspaceId,
        string query,
        List<(CodingFileEntry File, float Score)> scored,
        CancellationToken ct)
    {
        try
        {
            var queryEmbedding = await _router.GetEmbeddingAsync(query, ct);
            if (queryEmbedding.Length == 0) return scored;

            var embeddings = await _store.GetFileEmbeddingsAsync(workspaceId, ct);
            if (embeddings.Count == 0) return scored;

            return scored
                .Select(item =>
                {
                    if (!embeddings.TryGetValue(item.File.Id, out var fileEmb) || fileEmb.Length == 0)
                    {
                        return item;
                    }

                    var sim = CosineSimilarity(queryEmbedding, fileEmb);
                    // Blend: 65% semantic + 35% heuristic. Semantic signal is trusted
                    // more heavily once embeddings are available — heuristic is a tie-breaker.
                    var blended = 0.65f * sim + 0.35f * item.Score;
                    return (item.File, blended);
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Embedding re-ranking failed (falling back to heuristic score).");
            return scored;
        }
    }

    /// <summary>
    /// Traverses the KG "defines" relation from each matched symbol back to its containing
    /// code-file entity and boosts that file's relevance score.
    /// </summary>
    private async Task<List<(CodingFileEntry File, float Score)>> ApplyKgFileBoostAsync(
        List<(CodingFileEntry File, float Score)> scored,
        IReadOnlyList<CodingKgHit> kgHits,
        CancellationToken ct)
    {
        const float KgFileBoost = 0.8f;

        var boostedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in kgHits)
        {
            if (string.IsNullOrWhiteSpace(hit.EntityId)) continue;
            try
            {
                // Get all code-file entities that "define" this symbol (incoming direction).
                var relations = await _memory.GetRelationsAsync(Access, hit.EntityId, relationType: "defines", incoming: true, ct: ct);
                foreach (var rel in relations)
                {
                    var fileEntity = await _memory.GetEntityAsync(Access, rel.FromEntityId, ct);
                    if (fileEntity is { EntityType: "code-file" })
                    {
                        boostedPaths.Add(fileEntity.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "KG file-boost traversal failed for symbol '{Name}' (non-fatal).", hit.Name);
            }
        }

        if (boostedPaths.Count == 0) return scored;

        _logger.LogDebug("KG file boost applied to {Count} file path(s).", boostedPaths.Count);

        return scored.Select(item =>
            boostedPaths.Contains(item.File.RelativePath)
                ? (item.File, item.Score + KgFileBoost)
                : item
        ).ToList();
    }

    private async Task<IReadOnlyList<CodingKgHit>> FetchKgHitsAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<CodingKgHit>();

        try
        {
            // Search per token so multi-word queries ("payment validate") find entities whose
            // names contain any of the terms ("PaymentGateway.Validate"). A single LIKE on the
            // full phrase would match nothing because entity names never contain spaces.
            var tokens = Tokenize(query);
            if (tokens.Length == 0) return Array.Empty<CodingKgHit>();

            var seen = new Dictionary<string, (KgEntity Entity, float Score)>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Math.Min(tokens.Length, 4); i++)
            {
                var results = await _memory.SearchEntitiesAsync(Access, tokens[i], domain: "code", limit: 5, ct: ct);
                var tokenWeight = 1.0f - (i * 0.15f); // earlier tokens = slightly higher weight
                foreach (var entity in results)
                {
                    if (!seen.ContainsKey(entity.Id))
                        seen[entity.Id] = (entity, tokenWeight);
                }
            }

            return seen.Values
                .OrderByDescending(x => x.Score)
                .Take(5)
                .Select((x, i) => new CodingKgHit
                {
                    EntityId = x.Entity.Id,
                    Name = x.Entity.Name,
                    EntityType = x.Entity.EntityType,
                    Domain = x.Entity.Domain,
                    Description = x.Entity.Description,
                    RelevanceScore = x.Score - (i * 0.05f)
                })
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "KG entity search failed (non-fatal).");
            return Array.Empty<CodingKgHit>();
        }
    }

    internal static float ScoreFile(CodingFileEntry file, string[] tokens)
    {
        var path = file.RelativePath.ToLowerInvariant();

        // Source and test files are primary; config/project files are supporting context.
        // This ordering matters: without any query, the agent should see source before metadata.
        var score = file.Kind switch
        {
            "source" => 0.55f,
            "test" => 0.50f,
            "config" => 0.30f,
            "documentation" => 0.20f,
            "project-config" => 0.15f,
            "build-config" => 0.12f,
            _ => 0.25f
        };

        // README is useful for project understanding, but only one of them.
        if (path.EndsWith("readme.md", StringComparison.OrdinalIgnoreCase)) score += 0.25f;
        if (tokens.Length == 0) return score;

        foreach (var token in tokens)
        {
            if (path.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 1.0f;
            }
        }

        return score;
    }

    private static async Task<string> ReadPreviewAsync(string rootPath, CodingFileEntry file, CancellationToken ct)
    {
        if (file.SizeBytes > MaxPreviewBytes)
        {
            return $"[Text file omitted from preview because it is {file.SizeBytes} bytes.]";
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, file.RelativePath));
            var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "[Preview refused because file resolved outside workspace root.]";
            }

            var text = await File.ReadAllTextAsync(fullPath, ct);
            return text.Length > MaxPreviewChars ? text[..MaxPreviewChars] + "\n[truncated]" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[Preview unavailable: {ex.Message}]";
        }
    }

    private static string[] Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();

        return query
            .Split(new[] { ' ', '\t', '\r', '\n', '.', ',', ':', ';', '/', '\\', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    internal static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-8f ? 0f : dot / denom;
    }
}
