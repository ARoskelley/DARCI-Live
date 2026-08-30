#nullable enable

using DarciControl.Logic.Nodes;

namespace DarciControl.Logic.Tests;

/// <summary>
/// What the node picker offers. Reads from disk rather than a running core, because you must be able to
/// build a distributable without booting DARCI first.
/// </summary>
public sealed class NodeCatalogTests : IDisposable
{
    private readonly string _dir;

    public NodeCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"darci-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteNode(string folder, string json)
    {
        var dir = Path.Combine(_dir, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "darci-node.json"), json);
    }

    [Fact]
    public void ListsAValidNodeWithItsCapabilities()
    {
        // The capabilities are what including a node actually BUYS, so the picker has to show them.
        WriteNode("acme", """
            {"contract_version":"0.1.1","node_id":"acme.summarize","display_name":"Acme Summarizer",
             "node_version":"2.1.0","kind":"capability",
             "capabilities":[{"name":"summarize.text","description":"d"}]}
            """);

        var entry = Assert.Single(NodeCatalog.Scan(_dir));

        Assert.Equal("acme.summarize", entry.NodeId);
        Assert.Equal("Acme Summarizer", entry.DisplayName);
        Assert.Equal("2.1.0", entry.Version);
        Assert.Equal(new[] { "summarize.text" }, entry.Capabilities);
        Assert.True(entry.IsSelectable);
        Assert.False(entry.IsOutOfProcess);
    }

    [Fact]
    public void MarksAnEndpointNodeAsOutOfProcess()
    {
        WriteNode("remote", """
            {"contract_version":"0.1.1","node_id":"acme.remote","display_name":"Remote","node_version":"1.0.0",
             "kind":"capability","endpoint":"http://localhost:7901",
             "capabilities":[{"name":"remote.do","description":"d"}]}
            """);

        Assert.True(Assert.Single(NodeCatalog.Scan(_dir)).IsOutOfProcess);
    }

    [Fact]
    public void ABrokenManifest_IsListedAsUnselectableRatherThanHidden()
    {
        // A node the user expects to see, silently absent, is worse than one shown with its reason.
        WriteNode("broken", "{ not json at all");

        var entry = Assert.Single(NodeCatalog.Scan(_dir));

        Assert.False(entry.IsSelectable);
        Assert.NotNull(entry.Problem);
    }

    [Fact]
    public void OneBrokenManifest_DoesNotHideTheGoodOnes()
    {
        // The tolerant load matters here: an exception would have emptied the whole picker.
        WriteNode("good", """
            {"contract_version":"0.1.1","node_id":"acme.good","display_name":"Good","node_version":"1.0.0",
             "kind":"capability","capabilities":[{"name":"good.do","description":"d"}]}
            """);
        WriteNode("bad", "{ not json");

        var entries = NodeCatalog.Scan(_dir);

        Assert.Equal(2, entries.Count);
        Assert.Single(entries, e => e.IsSelectable);
    }

    [Fact]
    public void AnEmptyOrMissingDirectory_IsEmpty_NotAnError()
    {
        Assert.Empty(NodeCatalog.Scan(_dir));
        Assert.Empty(NodeCatalog.Scan(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void TheRealRepoNodes_AreAllSelectable()
    {
        // Pins the shipped manifests: if one stops parsing, the picker would quietly offer fewer nodes.
        var nodes = Path.Combine(RequiredModelsTests.FindRepoRoot(), "DARCI-v4", "nodes");
        var entries = NodeCatalog.Scan(nodes);

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.IsSelectable, $"{e.NodeId}: {e.Problem}"));
        Assert.Contains(entries, e => e.NodeId == "darci.coding");
    }
}
