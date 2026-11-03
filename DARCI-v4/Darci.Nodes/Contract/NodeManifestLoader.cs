#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// Loads `darci-node.json` manifests from a `nodes/` directory (doc §5.5 step 1, decision D2: a static
/// scan — "Deterministic, reviewable, no rogue registration").
///
/// <para><b>Phase E capability invariant (§14c).</b> There is deliberately NO self-registration path: a node
/// cannot talk its way into the routing table at runtime. Extending the capability surface requires a
/// manifest file that a human reviewed and merged — that merge IS the human-authored act. The loader only
/// reads what is already on disk, and registration records the manifest's SHA-256 so any later change to the
/// capability surface is auditable after the fact.</para>
/// </summary>
public sealed class NodeManifestLoader
{
    public const string ManifestFileName = "darci-node.json";

    private readonly ILogger<NodeManifestLoader> _logger;

    public NodeManifestLoader(ILogger<NodeManifestLoader> logger) => _logger = logger;

    /// <summary>
    /// Scan <paramref name="nodesDirectory"/> for `darci-node.json` files (one level of subdirectories, plus
    /// the directory itself). Returns manifests keyed by their source path. A malformed or invalid manifest
    /// throws — a broken capability surface must be loud at startup, not silent at runtime.
    /// </summary>
    public IReadOnlyList<LoadedManifest> LoadAll(string nodesDirectory)
    {
        var results = new List<LoadedManifest>();
        if (!Directory.Exists(nodesDirectory))
        {
            _logger.LogInformation("No nodes directory at {Dir}; only code-registered nodes will be available.", nodesDirectory);
            return results;
        }

        var files = Directory.EnumerateFiles(nodesDirectory, ManifestFileName, SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
            results.Add(Load(file));

        _logger.LogInformation("Loaded {Count} node manifest(s) from {Dir}.", results.Count, nodesDirectory);
        return results;
    }

    /// <summary>
    /// Scan like <see cref="LoadAll"/>, but COLLECT failures instead of throwing on the first bad file.
    ///
    /// <para>Phase 3 Fork 2: a third party's broken manifest must never brick this core (requirement A),
    /// while a first-party one still has to be loud and fatal. Those are different verdicts about the same
    /// kind of file, and only the caller knows which is which — it is the one that can tell whether a
    /// manifest's node_id matches something compiled in. So the loader reports; discovery decides.</para>
    /// </summary>
    public (IReadOnlyList<LoadedManifest> Loaded, IReadOnlyList<ManifestLoadFailure> Failures) LoadAllTolerant(
        string nodesDirectory)
    {
        var loaded = new List<LoadedManifest>();
        var failures = new List<ManifestLoadFailure>();

        if (!Directory.Exists(nodesDirectory))
        {
            _logger.LogInformation("No nodes directory at {Dir}; only code-registered nodes will be available.", nodesDirectory);
            return (loaded, failures);
        }

        var files = Directory.EnumerateFiles(nodesDirectory, ManifestFileName, SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                loaded.Add(Load(file));
            }
            catch (NodeRegistrationException ex)
            {
                failures.Add(new ManifestLoadFailure(file, ex.Message, TryReadNodeId(file)));
            }
        }

        _logger.LogInformation("Loaded {Count} node manifest(s) from {Dir} ({Failed} unreadable).",
            loaded.Count, nodesDirectory, failures.Count);
        return (loaded, failures);
    }

    /// <summary>
    /// Best-effort node_id from a manifest that failed to load, so a failure can still be ATTRIBUTED.
    /// Returns null when the file is too broken to say anything about — in which case it cannot be shown
    /// to be first-party, and discovery treats it as foreign.
    /// </summary>
    private static string? TryReadNodeId(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("node_id", out var id) ? id.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Load and validate one manifest file.</summary>
    public LoadedManifest Load(string path)
    {
        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex)
        {
            throw new NodeRegistrationException($"Could not read manifest '{path}': {ex.Message}");
        }

        NodeManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<NodeManifest>(raw, ManifestJson.Options); }
        catch (JsonException ex)
        {
            throw new NodeRegistrationException($"Manifest '{path}' is not valid JSON: {ex.Message}");
        }

        if (manifest is null)
            throw new NodeRegistrationException($"Manifest '{path}' deserialized to null.");

        var errors = manifest.Validate();
        if (errors.Count > 0)
            throw new NodeRegistrationException($"Manifest '{path}' is invalid: {string.Join(" | ", errors)}");

        return new LoadedManifest(manifest, path, manifest.ComputeSha256());
    }
}

public sealed record LoadedManifest(NodeManifest Manifest, string SourcePath, string Sha256);

/// <summary>A manifest file that could not be loaded, with enough context to attribute it (Phase 3 Fork 2).</summary>
/// <param name="Path">Where the bad file is.</param>
/// <param name="Reason">Why it could not be loaded — quoted verbatim in logs so it is actionable.</param>
/// <param name="DeclaredNodeId">Its node_id if readable at all; null when the file is too broken to attribute.</param>
public sealed record ManifestLoadFailure(string Path, string Reason, string? DeclaredNodeId);
