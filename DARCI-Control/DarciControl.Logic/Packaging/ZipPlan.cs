#nullable enable

using DarciControl.Logic.Nodes;

namespace DarciControl.Logic.Packaging;

/// <summary>What to build. Everything the user chose, and nothing about how it gets written.</summary>
public sealed record ZipBuildRequest
{
    /// <summary>Repo root the zip is built FROM.</summary>
    public required string RepoRoot { get; init; }

    /// <summary>Where the .zip goes.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Node ids to include. EMPTY IS VALID — a bare core is a supported product (Phase 3).</summary>
    public IReadOnlyList<string> SelectedNodeIds { get; init; } = Array.Empty<string>();

    /// <summary>Ship the trained ONNX models. Off by default: they are large, gitignored ("deploy
    /// separately"), and the core degrades to its priority ladder without them.</summary>
    public bool IncludeOnnxModels { get; init; }

    /// <summary>Publish RID. Self-contained, so the target machine needs no .NET SDK.</summary>
    public string Runtime { get; init; } = "win-x64";
}

/// <summary>One file or folder that will be written into the zip, and where.</summary>
/// <param name="Source">Absolute source path.</param>
/// <param name="EntryPath">Path inside the zip, forward-slashed.</param>
/// <param name="IsDirectory">Whether Source is a directory to copy recursively.</param>
public sealed record ZipEntry(string Source, string EntryPath, bool IsDirectory);

/// <summary>
/// The PLAN: exactly what a zip will contain, computed without touching the output.
///
/// <para>Separated from the writing on purpose. Assembling a zip needs a self-contained publish, which
/// takes minutes and cannot be unit-tested at any sensible speed; deciding what belongs in it is pure and
/// is where the mistakes that actually matter live — shipping a secret, omitting the profile, silently
/// dropping a node the user ticked. So the decisions are testable and the I/O is thin.</para>
/// </summary>
public sealed record ZipPlan
{
    public required IReadOnlyList<ZipEntry> Entries { get; init; }
    public required IReadOnlyList<string> IncludedNodeIds { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Files that must NEVER be packaged, whatever else changes.</summary>
    public static IReadOnlyList<string> ForbiddenNames { get; } = new[] { ".env.local", ".env.engineering.local" };

    public static ZipPlan Create(ZipBuildRequest request, IReadOnlyList<NodeCatalogEntry> catalog, string publishedCoreDir)
    {
        var entries = new List<ZipEntry>();
        var warnings = new List<string>();
        var included = new List<string>();

        // The core: a self-contained publish, so the target needs no .NET SDK installed.
        entries.Add(new ZipEntry(publishedCoreDir, "darci/core", IsDirectory: true));

        // Selected nodes only. An unticked node is not merely unregistered — it is not in the zip at all.
        foreach (var id in request.SelectedNodeIds)
        {
            var entry = catalog.FirstOrDefault(e => string.Equals(e.NodeId, id, StringComparison.Ordinal));
            if (entry is null)
            {
                warnings.Add($"Node '{id}' was selected but is not in the catalog; skipped.");
                continue;
            }

            if (!entry.IsSelectable)
            {
                warnings.Add($"Node '{id}' cannot be packaged: {entry.Problem}");
                continue;
            }

            entries.Add(new ZipEntry(entry.FolderPath, $"darci/nodes/{entry.NodeId}", IsDirectory: true));
            included.Add(entry.NodeId);
        }

        if (included.Count == 0)
        {
            // Not a warning — Phase 3 made a node-free core a real, working product, and saying so here
            // stops the UI implying the user forgot something.
            warnings.Add("No nodes selected — this is a valid bare core. It will run and honestly report "
                       + "that no capabilities are available.");
        }

        // The host profile IS the model contract. Without it the packaged startup script cannot know what
        // to check for, and the core falls back to env-compat defaults that may not match this machine.
        var profile = Path.Combine(request.RepoRoot, "DARCI-v4", "host-profile.json");
        if (File.Exists(profile)) entries.Add(new ZipEntry(profile, "darci/host-profile.json", false));
        else warnings.Add("host-profile.json was not found; the zip will rely on env-compat model defaults.");

        // The EXAMPLE, never the real thing — .env.local holds the Neo4j password.
        var envExample = Path.Combine(request.RepoRoot, ".env.local.example");
        if (File.Exists(envExample)) entries.Add(new ZipEntry(envExample, "darci/.env.local.example", false));

        // The model resolver ships as-is: it already handles the packaged layout, and the launcher below
        // calls it so the zip's prerequisite check derives from the profile shipped beside it.
        var resolver = Path.Combine(request.RepoRoot, "Get-DarciRequiredModels.ps1");
        if (File.Exists(resolver)) entries.Add(new ZipEntry(resolver, "darci/Get-DarciRequiredModels.ps1", false));
        else warnings.Add("Get-DarciRequiredModels.ps1 was not found; the packaged launcher will skip its model check.");

        // NOT copied from the repo: Start-DARCI.ps1 resolves DARCI-v4\Darci.Api and uses `dotnet run`,
        // neither of which exists in a zip — it would hand the recipient a launcher that cannot launch.
        // Test-DARCIEnvironment.ps1 is likewise repo-shaped (it checks for the solution and a DARCI-v4
        // folder) and would report confident failures about a perfectly good install.
        // The launcher is GENERATED for this layout instead; see PackagedStartScript.

        if (request.IncludeOnnxModels)
        {
            var models = Path.Combine(request.RepoRoot, "DARCI-v4", "Darci.Brain.Training", "models");
            if (Directory.Exists(models)) entries.Add(new ZipEntry(models, "darci/core/Models", true));
            else warnings.Add("ONNX models were requested but none were found; the core will use its priority ladder.");
        }

        return new ZipPlan { Entries = entries, IncludedNodeIds = included, Warnings = warnings };
    }

    /// <summary>
    /// The last line of defence before writing. A secret leaving this machine inside a zip somebody else
    /// unpacks is not a bug you get to fix later, so it is checked here as well as avoided above.
    /// </summary>
    public IReadOnlyList<string> FindForbidden() =>
        Entries
            .Where(e => ForbiddenNames.Any(f =>
                string.Equals(Path.GetFileName(e.Source), f, StringComparison.OrdinalIgnoreCase) ||
                e.EntryPath.EndsWith("/" + f, StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.EntryPath)
            .ToList();
}
