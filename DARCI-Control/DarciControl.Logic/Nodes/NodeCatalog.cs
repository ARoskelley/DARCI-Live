#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DarciControl.Logic.Nodes;

/// <summary>One node the user can choose to include in a distributable zip.</summary>
/// <param name="NodeId">Canonical id, e.g. <c>darci.coding</c>.</param>
/// <param name="DisplayName">Human label for the picker.</param>
/// <param name="Version">The node's own semver.</param>
/// <param name="Capabilities">The verbs it serves — what including it actually buys.</param>
/// <param name="FolderPath">The folder to copy into the zip.</param>
/// <param name="IsOutOfProcess">True when the manifest declares an endpoint.</param>
/// <param name="Problem">Why it is not selectable, when it is not.</param>
public sealed record NodeCatalogEntry(
    string NodeId,
    string DisplayName,
    string Version,
    IReadOnlyList<string> Capabilities,
    string FolderPath,
    bool IsOutOfProcess,
    string? Problem = null)
{
    public bool IsSelectable => Problem is null;
}

/// <summary>
/// What is available to put in a zip, read straight from the <c>nodes/</c> directory.
///
/// <para>Reads from DISK, not from a running core, which is the right call for a packaging tool: you must
/// be able to build a distributable without booting DARCI first. The core's <c>/nodes</c> endpoint answers
/// the different question of what is live right now.</para>
///
/// <para>Uses the core's own <see cref="NodeManifestLoader"/> so the picker cannot disagree with the core
/// about what a node is — and its tolerant load means one malformed manifest lists as unselectable instead
/// of hiding every other node behind an exception.</para>
/// </summary>
public static class NodeCatalog
{
    public static IReadOnlyList<NodeCatalogEntry> Scan(string nodesDirectory)
    {
        var entries = new List<NodeCatalogEntry>();
        if (!Directory.Exists(nodesDirectory)) return entries;

        var loader = new NodeManifestLoader(NullLogger<NodeManifestLoader>.Instance);
        var (loaded, failures) = loader.LoadAllTolerant(nodesDirectory);

        foreach (var m in loaded)
        {
            var folder = Path.GetDirectoryName(m.SourcePath)!;
            entries.Add(new NodeCatalogEntry(
                m.Manifest.NodeId,
                string.IsNullOrWhiteSpace(m.Manifest.DisplayName) ? m.Manifest.NodeId : m.Manifest.DisplayName,
                m.Manifest.NodeVersion,
                m.Manifest.Capabilities.Select(c => c.Name).ToList(),
                folder,
                !m.Manifest.IsInProcess));
        }

        // Surfaced rather than dropped: a node the user expects to see, silently absent from the picker,
        // is a worse experience than one listed with the reason it cannot be packaged.
        foreach (var f in failures)
        {
            entries.Add(new NodeCatalogEntry(
                f.DeclaredNodeId ?? Path.GetFileName(Path.GetDirectoryName(f.Path)!),
                f.DeclaredNodeId ?? "(unreadable manifest)",
                "?",
                Array.Empty<string>(),
                Path.GetDirectoryName(f.Path)!,
                IsOutOfProcess: false,
                Problem: f.Reason));
        }

        return entries.OrderBy(e => e.NodeId, StringComparer.Ordinal).ToList();
    }
}
