#nullable enable

using System.Text.RegularExpressions;
using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Resolves which coding workspace an autonomous coding goal should run in (the workspace-selection
/// seam). Heuristic: embed the goal, score it against each existing workspace's file embeddings
/// (max cosine similarity), and reuse the best match when it clears <see cref="ReuseThreshold"/>;
/// otherwise create a fresh workspace. The match score is carried as the unified
/// <see cref="Confidence"/> so the "not confident enough to reuse" case is an explicit gap.
///
/// This is the coding implementation of the generic <see cref="IWorkContextResolver"/> pattern;
/// engineering/biomed nodes will supply their own resolvers with the same shape.
/// </summary>
public sealed class CodingWorkspaceResolver : IWorkContextResolver
{
    /// <summary>Max-cosine similarity at/above which an existing workspace is reused rather than creating a new one.</summary>
    public const double ReuseThreshold = 0.75;

    private readonly ICodingWorkspaceStore _store;
    private readonly IModelRouter _router;
    private readonly IWorkspaceScanner _scanner;
    private readonly string _workspacesRoot;
    private readonly ILogger<CodingWorkspaceResolver> _logger;

    public CodingWorkspaceResolver(
        ICodingWorkspaceStore store,
        IModelRouter router,
        IWorkspaceScanner scanner,
        string workspacesRoot,
        ILogger<CodingWorkspaceResolver> logger)
    {
        _store = store;
        _router = router;
        _scanner = scanner;
        _workspacesRoot = workspacesRoot;
        _logger = logger;
    }

    public async Task<WorkContextResolution> ResolveAsync(string intent, CancellationToken ct = default)
    {
        var workspaces = await _store.GetWorkspacesAsync(200, ct);

        // Nothing to match against — create.
        if (workspaces.Count == 0)
            return await CreateAsync(intent, Confidence.Unassessed,
                "No existing workspaces; created a fresh one.", ct);

        // Embed the goal. If embeddings are unavailable we cannot assess similarity — rather than risk
        // writing into the wrong existing codebase, create a fresh workspace and record the gap.
        var goalEmbedding = await _router.GetEmbeddingAsync(intent, ct);
        if (goalEmbedding.Length == 0)
            return await CreateAsync(intent, Confidence.Unassessed,
                "Could not assess similarity (embedding unavailable); created a fresh workspace.", ct);

        // Gather each workspace's file embeddings and score by best (max-cosine) match.
        var bundles = new List<(string WorkspaceId, IReadOnlyList<float[]> Embeddings)>(workspaces.Count);
        foreach (var ws in workspaces)
        {
            var embeddings = await _store.GetFileEmbeddingsAsync(ws.Id, ct);
            bundles.Add((ws.Id, embeddings.Values.ToList()));
        }

        var (bestId, bestScore) = SelectBestMatch(goalEmbedding, bundles);
        var confidence = Confidence.Of(bestScore);

        if (bestId is not null && bestScore >= ReuseThreshold)
        {
            var name = workspaces.FirstOrDefault(w => w.Id == bestId)?.Name ?? bestId;
            _logger.LogInformation("Workspace resolved by reuse: {Name} (similarity {Score:F2}).", name, bestScore);
            return new WorkContextResolution(bestId, Created: false, confidence,
                $"Reused workspace '{name}' (best match similarity {bestScore:F2} >= {ReuseThreshold:F2}).");
        }

        return await CreateAsync(intent, confidence,
            $"Best existing match {bestScore:F2} below reuse threshold {ReuseThreshold:F2}; created a fresh workspace.", ct);
    }

    private async Task<WorkContextResolution> CreateAsync(string intent, Confidence confidence, string reasoning, CancellationToken ct)
    {
        var slug = Slug(intent);
        var rootPath = Path.Combine(_workspacesRoot, slug);
        Directory.CreateDirectory(rootPath);

        var result = await _scanner.ImportAsync(
            new CodingWorkspaceImportRequest(rootPath, Name: slug, CreatedBy: "DARCI", Tags: new[] { "auto-created" }), ct);

        _logger.LogInformation("Workspace created for coding goal: {Id} at {Path}.", result.Workspace.Id, rootPath);
        return new WorkContextResolution(result.Workspace.Id, Created: true, confidence, reasoning);
    }

    /// <summary>
    /// Pure scoring core: returns the workspace with the highest max-cosine similarity to the goal
    /// embedding, and that score (clamped to [0,1]; a workspace with no embeddings scores 0).
    /// </summary>
    internal static (string? WorkspaceId, double Score) SelectBestMatch(
        float[] goalEmbedding,
        IReadOnlyList<(string WorkspaceId, IReadOnlyList<float[]> Embeddings)> candidates)
    {
        string? bestId = null;
        var bestScore = 0.0;
        foreach (var (workspaceId, embeddings) in candidates)
        {
            var score = 0.0;
            foreach (var emb in embeddings)
                score = Math.Max(score, Cosine(goalEmbedding, emb));
            if (bestId is null || score > bestScore)
            {
                bestId = workspaceId;
                bestScore = score;
            }
        }
        return (bestId, bestScore);
    }

    /// <summary>Cosine similarity clamped to [0,1] (negatives → 0, i.e. dissimilar).</summary>
    internal static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0.0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0.0;
        var cos = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        return cos < 0 ? 0.0 : cos > 1 ? 1.0 : cos;
    }

    /// <summary>Filesystem-safe directory name from the goal text, with a timestamp to avoid collisions.</summary>
    internal static string Slug(string intent)
    {
        var lowered = (intent ?? "").ToLowerInvariant();
        var cleaned = Regex.Replace(lowered, "[^a-z0-9]+", "-").Trim('-');
        if (cleaned.Length > 40) cleaned = cleaned[..40].Trim('-');
        if (cleaned.Length == 0) cleaned = "coding-task";
        return $"{cleaned}-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }
}
