#nullable enable

using Darci.Memory.Graph;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

public sealed class CodingContextBuilder : ICodingContextBuilder
{
    private const int MaxPreviewBytes = 200_000;
    private const int MaxPreviewChars = 4_000;

    private readonly ICodingWorkspaceStore _store;
    private readonly IModelRouter _router;
    private readonly IKnowledgeGraph _kg;
    private readonly ILogger<CodingContextBuilder> _logger;

    public CodingContextBuilder(
        ICodingWorkspaceStore store,
        IModelRouter router,
        IKnowledgeGraph kg,
        ILogger<CodingContextBuilder> logger)
    {
        _store = store;
        _router = router;
        _kg = kg;
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

        // Fetch KG hits for the query.
        var kgHits = await FetchKgHitsAsync(query, ct);

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
                    // Blend: 40% cosine similarity + 60% existing heuristic score.
                    var blended = 0.4f * sim + 0.6f * item.Score;
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

    private async Task<IReadOnlyList<CodingKgHit>> FetchKgHitsAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<CodingKgHit>();

        try
        {
            var entities = await _kg.SearchEntitiesAsync(query, domain: "code", limit: 5, ct: ct);
            return entities
                .Select((e, i) => new CodingKgHit
                {
                    EntityId = e.Id,
                    Name = e.Name,
                    EntityType = e.EntityType,
                    Domain = e.Domain,
                    Description = e.Description,
                    RelevanceScore = 1.0f - (i * 0.1f)
                })
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "KG entity search failed (non-fatal).");
            return Array.Empty<CodingKgHit>();
        }
    }

    private static float ScoreFile(CodingFileEntry file, string[] tokens)
    {
        var path = file.RelativePath.ToLowerInvariant();
        var score = file.Kind switch
        {
            "project-config" => 0.85f,
            "build-config" => 0.8f,
            "documentation" => 0.6f,
            "test" => 0.55f,
            _ => 0.25f
        };

        if (path.EndsWith("readme.md", StringComparison.OrdinalIgnoreCase)) score += 0.5f;
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

    private static float CosineSimilarity(float[] a, float[] b)
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
