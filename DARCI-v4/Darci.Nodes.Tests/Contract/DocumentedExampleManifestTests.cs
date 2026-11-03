#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// Pins the claims in <c>docs/NODE_REQUIREMENTS.md</c> to the code they describe.
///
/// <para>A contract specification that drifts from the implementation is worse than none, because a node
/// author trusts it and is then rejected for a reason the document denies. So the parts of that document
/// an author depends on most are asserted here rather than reviewed by eye: its worked example actually
/// loads and registers, its capability-name table matches the real validator, its loopback table matches
/// the real gate, and its description of the manifest hash — semantic equality of the parsed manifest, NOT
/// byte-for-byte as <c>nodes/README.md</c> claims — is demonstrated in both directions.</para>
/// </summary>
public sealed class DocumentedExampleManifestTests : IDisposable
{
    private readonly string _dir;

    public DocumentedExampleManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"darci-doc-{Guid.NewGuid():N}", "acme.summarize");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "darci-node.json"), Manifest);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
    }

    // Copied verbatim from docs/NODE_REQUIREMENTS.md section 14.2.
    private const string Manifest = """
{
  "contract_version": "0.1.1",
  "node_id": "acme.summarize",
  "display_name": "Summariser",
  "node_version": "0.1.0",
  "kind": "capability",
  "endpoint": "http://127.0.0.1:7802",
  "health": "/health",
  "capabilities": [
    {
      "name": "acme.summarize",
      "description": "Summarise a block of text. Deterministic: extractive, no model class consulted.",
      "input_schema": {
        "type": "object",
        "required": ["text"],
        "description": "The node contract carries payloads as a flat map of string to string, so every slot below is a STRING.",
        "properties": {
          "text": { "type": "string", "description": "The text to summarise." },
          "max_sentences": { "type": "string", "description": "Decimal string. Defaults to '3'." }
        }
      },
      "output_schema": {
        "type": "object",
        "description": "Also a flat map of string to string; numbers are decimal strings.",
        "properties": {
          "summary": { "type": "string" },
          "sentence_count": { "type": "string" },
          "input_sha256": { "type": "string", "description": "So a summary is reproducible from its input." }
        }
      },
      "typical_latency_ms": 200,
      "deadline_ms": 30000
    }
  ],
  "requires": {
    "model_classes": [],
    "memory_scopes": [],
    "permissions": [],
    "emits_untrusted": false
  },
  "author": "you",
  "repository": "https://github.com/you/acme-summarize"
}
""";

    [Fact]
    public void TheDocumentedExampleManifest_LoadsAndValidates()
    {
        var loader = new NodeManifestLoader(NullLogger<NodeManifestLoader>.Instance);

        var loaded = loader.Load(Path.Combine(_dir, "darci-node.json"));

        Assert.Empty(loaded.Manifest.Validate());
        Assert.Equal("acme.summarize", loaded.Manifest.NodeId);
        Assert.Equal(NodeKind.Capability, loaded.Manifest.Kind);
        Assert.False(loaded.Manifest.IsInProcess);
        Assert.Equal(30_000, loaded.Manifest.Capabilities[0].DeadlineMs);
        Assert.Equal(64, loaded.Sha256.Length);
    }

    [Fact]
    public void TheDocumentedExampleManifest_RegistersAndIsRoutable()
    {
        var loaded = new NodeManifestLoader(NullLogger<NodeManifestLoader>.Instance)
            .Load(Path.Combine(_dir, "darci-node.json"));
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);

        registry.Register(new HttpNodeAdapter(loaded.Manifest, new HttpClient(), NullLogger<HttpNodeAdapter>.Instance));

        Assert.NotNull(registry.Resolve("acme.summarize"));
        Assert.Contains("acme.summarize", registry.RoutableCapabilities);
    }

    [Fact]
    public void TheDocumentedEndpointIsAcceptedAsLoopback()
    {
        // Verifies the doc's claim in section 13.1 about which endpoints pass the gate.
        Assert.True(NodeDiscovery.IsLoopback("http://127.0.0.1:7802"));
        Assert.True(NodeDiscovery.IsLoopback("http://localhost:7802"));
        Assert.True(NodeDiscovery.IsLoopback("http://[::1]:7802"));
        Assert.False(NodeDiscovery.IsLoopback("https://nodes.example.com"));
        Assert.False(NodeDiscovery.IsLoopback("http://192.168.1.50:7802"));
    }

    [Theory]
    // Section 4.1's table, checked against the real validator.
    [InlineData("model.build", true)]
    [InlineData("acme.summarize_text", true)]
    [InlineData("a.b.c", true)]
    [InlineData("Model.Build", false)]
    [InlineData("model-build", false)]
    [InlineData("build", false)]
    [InlineData("model.2d", false)]
    [InlineData("model..build", false)]
    public void TheDocumentedCapabilityNameTable_MatchesTheValidator(string name, bool expected)
    {
        Assert.Equal(expected, CapabilityKey.IsValidName(name));
    }

    [Fact]
    public void TheDocumentedSupportedContractVersions_AreTheRealOnes()
    {
        Assert.True(NodeContractVersion.IsSupported("0.1"));
        Assert.True(NodeContractVersion.IsSupported("0.1.1"));
        Assert.False(NodeContractVersion.IsSupported("0.2"));
        Assert.Equal("0.1.1", NodeContractVersion.Current);
    }

    [Fact]
    public void TheDocumentedHashSemantics_AreSemanticNotByteForByte()
    {
        // Section 5.4: reformatting and reordering TOP-LEVEL keys does not change the hash, but reordering
        // keys INSIDE input_schema does, because those are carried as raw JsonElements.
        var loader = new NodeManifestLoader(NullLogger<NodeManifestLoader>.Instance);
        var original = loader.Load(Path.Combine(_dir, "darci-node.json")).Sha256;

        var reformatted = Path.Combine(_dir, "reformatted.json");
        File.WriteAllText(reformatted, Manifest
            .Replace("\n", "\n\n")                                        // whitespace
            .Replace("\"contract_version\": \"0.1.1\",\n", "")            // move a top-level key
            .TrimEnd().TrimEnd('}') + ",\n\"contract_version\":\"0.1.1\"}");
        Assert.Equal(original, loader.Load(reformatted).Sha256);

        var reordered = Path.Combine(_dir, "reordered.json");
        File.WriteAllText(reordered, Manifest.Replace(
            "\"type\": \"object\",\n        \"required\": [\"text\"],",
            "\"required\": [\"text\"],\n        \"type\": \"object\","));
        Assert.NotEqual(original, loader.Load(reordered).Sha256);
    }

    [Fact]
    public void TheDocumentedStatePredicates_Behave()
    {
        // Section 9.1's three predicates.
        Assert.True(NodeState.Blocked.IsTerminal());
        Assert.False(NodeState.Blocked.IsFailure());
        Assert.False(NodeState.Blocked.IsSuccess());
        Assert.False(NodeState.AwaitingDependency.IsTerminal());
        Assert.Equal(8, (int)NodeState.Blocked);
        Assert.Equal(4, (int)NodeState.AwaitingDependency);
    }
}
