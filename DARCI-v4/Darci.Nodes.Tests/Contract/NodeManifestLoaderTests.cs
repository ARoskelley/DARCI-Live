using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

public sealed class NodeManifestLoaderTests : IDisposable
{
    private readonly string _dir;
    private readonly NodeManifestLoader _loader = new(NullLogger<NodeManifestLoader>.Instance);

    public NodeManifestLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"darci-nodes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteManifest(string nodeDir, string json)
    {
        var dir = Path.Combine(_dir, nodeDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, NodeManifestLoader.ManifestFileName);
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidJson = """
        {
          "contract_version": "0.1.1",
          "node_id": "acme.thermal",
          "display_name": "Thermal Sim",
          "node_version": "2.1.0",
          "kind": "capability",
          "endpoint": "http://localhost:8500",
          "capabilities": [
            { "name": "acme.simulate_thermal", "description": "Run a thermal sim.", "deadline_ms": 60000 }
          ],
          "requires": {
            "model_classes": ["chat.fast"],
            "memory_scopes": ["read:documents"],
            "permissions": [],
            "emits_untrusted": false
          },
          "health": "/health"
        }
        """;

    [Fact]
    public void Load_ParsesTheDocsDarciNodeJsonShape()
    {
        var path = WriteManifest("acme.thermal", ValidJson);
        var loaded = _loader.Load(path);

        Assert.Equal("acme.thermal", loaded.Manifest.NodeId);
        Assert.Equal("2.1.0", loaded.Manifest.NodeVersion);
        Assert.Equal(NodeKind.Capability, loaded.Manifest.Kind);
        Assert.Equal("acme.simulate_thermal", loaded.Manifest.Capabilities[0].Name);
        Assert.Equal(60000, loaded.Manifest.Capabilities[0].DeadlineMs);
        Assert.Equal(new[] { "chat.fast" }, loaded.Manifest.Requires.ModelClasses);
        Assert.False(loaded.Manifest.IsInProcess);           // it declared an endpoint
        Assert.Equal(loaded.Manifest.ComputeSha256(), loaded.Sha256);
        Assert.Equal(path, loaded.SourcePath);
    }

    [Fact]
    public void LoadAll_ScansSubdirectories_Deterministically()
    {
        WriteManifest("b.node", ValidJson.Replace("acme.thermal", "b.node").Replace("acme.simulate_thermal", "b.do_thing"));
        WriteManifest("a.node", ValidJson.Replace("acme.thermal", "a.node").Replace("acme.simulate_thermal", "a.do_thing"));

        var all = _loader.LoadAll(_dir);
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "a.node", "b.node" }, all.Select(m => m.Manifest.NodeId).ToArray());   // path-ordered
    }

    [Fact]
    public void MissingDirectory_IsNotAnError_JustNoManifests()
        => Assert.Empty(_loader.LoadAll(Path.Combine(_dir, "does-not-exist")));

    [Fact]
    public void MalformedJson_ThrowsNamedException()
    {
        var path = WriteManifest("bad.json", "{ this is not json ");
        var ex = Assert.Throws<NodeRegistrationException>(() => _loader.Load(path));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void InvalidManifest_ThrowsNamedException_LoudAtStartupNotSilentAtRuntime()
    {
        var path = WriteManifest("bad.caps", ValidJson.Replace("acme.simulate_thermal", "NotNamespaced"));
        var ex = Assert.Throws<NodeRegistrationException>(() => _loader.Load(path));
        Assert.Contains("is invalid", ex.Message);
        Assert.Contains("NotNamespaced", ex.Message);
    }

    [Fact]
    public void TheRepoBuiltInManifests_AreValid_AndCoverTheBuiltInNodes()
    {
        // The real ones committed under DARCI-v4/nodes/ — the reviewed artifacts that constitute the
        // human-authored capability grant (Phase E §14c).
        var repoNodesDir = FindRepoNodesDirectory();
        Assert.NotNull(repoNodesDir);

        var all = _loader.LoadAll(repoNodesDir!);
        var byId = all.ToDictionary(m => m.Manifest.NodeId, StringComparer.Ordinal);

        Assert.Contains(NodeKeys.Coding, byId.Keys);
        Assert.Contains(NodeKeys.Knowledge, byId.Keys);
        Assert.Contains(NodeKeys.Innovation, byId.Keys);
        Assert.All(all, m => Assert.Empty(m.Manifest.Validate()));

        // Every built-in capability must be claimed by exactly one manifest.
        var declared = all.SelectMany(m => m.Manifest.Capabilities.Select(c => c.Name)).ToList();
        foreach (var expected in new[]
                 {
                     Capabilities.CodingWrite, Capabilities.CodingTest,
                     Capabilities.KnowledgeAnswer, Capabilities.KnowledgeGapFill,
                     Capabilities.InnovationSynthesize,
                 })
            Assert.Single(declared, d => d == expected);

        // In-process in Phase 1 (no endpoints yet).
        Assert.All(all, m => Assert.True(m.Manifest.IsInProcess));

        // ADD-5b: the requires inventory is real, not empty boilerplate.
        Assert.All(all, m => Assert.NotEmpty(m.Manifest.Requires.ModelClasses));
        Assert.All(all, m => Assert.NotEmpty(m.Manifest.Requires.MemoryScopes));

        // The knowledge node fetches outside content, so it must declare it (doc §4.3/§7).
        Assert.True(byId[NodeKeys.Knowledge].Manifest.Requires.EmitsUntrusted);

        // The coding node owns a durable workspace ⇒ environment kind with a workspace_root (doc §8).
        Assert.Equal(NodeKind.Environment, byId[NodeKeys.Coding].Manifest.Kind);
        Assert.False(string.IsNullOrWhiteSpace(byId[NodeKeys.Coding].Manifest.WorkspaceRoot));
    }

    /// <summary>Walk up from the test assembly to find DARCI-v4/nodes.</summary>
    private static string? FindRepoNodesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "nodes");
            if (Directory.Exists(candidate) &&
                Directory.EnumerateFiles(candidate, NodeManifestLoader.ManifestFileName, SearchOption.AllDirectories).Any())
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
