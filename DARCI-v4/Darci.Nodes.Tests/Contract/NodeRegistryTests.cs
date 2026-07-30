using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>SU2 — the string-keyed registry. The C1 proof lives here: a capability the core was never
/// compiled with is routable purely from a manifest.</summary>
public class NodeRegistryTests
{
    private sealed class StubAdapter : INodeAdapter
    {
        public NodeManifest Manifest { get; }
        public StubAdapter(NodeManifest manifest) => Manifest = manifest;
        public Task<NodeResult> InvokeAsync(NodeInvocation invocation, CancellationToken ct = default)
            => Task.FromResult(NodeResult.Ok(invocation.TraceId));
    }

    private static NodeManifest Manifest(string nodeId, params string[] capabilities) => new()
    {
        NodeId = nodeId,
        DisplayName = nodeId,
        NodeVersion = "1.0.0",
        Capabilities = capabilities.Select(c => new NodeCapabilityDescriptor { Name = c, Description = c }).ToList(),
    };

    private static NodeRegistry Registry() => new(NullLogger<NodeRegistry>.Instance);

    private static INodeAdapter Adapter(string nodeId, params string[] capabilities)
        => new StubAdapter(Manifest(nodeId, capabilities));

    [Fact]
    public void Register_ThenResolveByCapabilityAndByNodeId()
    {
        var r = Registry();
        r.Register(Adapter(NodeKeys.Coding, Capabilities.CodingWrite, Capabilities.CodingTest));

        Assert.Equal(NodeKeys.Coding, r.Resolve(Capabilities.CodingWrite)!.NodeId);
        Assert.Equal(NodeKeys.Coding, r.Resolve(Capabilities.CodingTest)!.NodeId);
        Assert.Equal(NodeKeys.Coding, r.ResolveNode(NodeKeys.Coding)!.NodeId);
        Assert.Equal(new[] { Capabilities.CodingTest, Capabilities.CodingWrite }, r.RoutableCapabilities);
    }

    // ── THE C1 PROOF ──

    [Fact]
    public void ExternalNode_WithANovelStringCapability_Registers_AndRoutes()
    {
        // No `Capability` enum member exists for this, and the core was never compiled with knowledge of it.
        var r = Registry();
        r.Register(Adapter("acme.thermal", "acme.simulate_thermal"));

        Assert.Equal("acme.thermal", r.Resolve("acme.simulate_thermal")!.NodeId);
        Assert.Null(CapabilityKey.ToLegacy("acme.simulate_thermal"));   // genuinely unknown to the enum world
    }

    [Fact]
    public void UnknownCapability_ResolvesToNull_NotAnException()
    {
        var r = Registry();
        r.Register(Adapter(NodeKeys.Coding, Capabilities.CodingWrite));
        Assert.Null(r.Resolve("nobody.serves_this"));
        Assert.Null(r.Resolve(""));
        Assert.Null(r.ResolveNode("darci.nope"));
    }

    // ── fatal + named registration failures (doc §5.5 step 2) ──

    [Fact]
    public void InvalidManifest_ThrowsNamedRegistrationException()
    {
        var r = Registry();
        var ex = Assert.Throws<NodeRegistrationException>(() => r.Register(Adapter("darci.bad", "NotNamespaced")));
        Assert.Contains("darci.bad", ex.Message);
        Assert.Contains("not a valid namespaced", ex.Message);
    }

    [Fact]
    public void UnsupportedContractVersion_RefusesRegistration()
    {
        var r = Registry();
        var manifest = Manifest("darci.future", Capabilities.CodingWrite) with { ContractVersion = "9.9" };
        var ex = Assert.Throws<NodeRegistrationException>(() => r.Register(new StubAdapter(manifest)));
        Assert.Contains("contract_version", ex.Message);
    }

    [Fact]
    public void DuplicateNodeId_IsRejected()
    {
        var r = Registry();
        r.Register(Adapter(NodeKeys.Coding, Capabilities.CodingWrite));
        var ex = Assert.Throws<NodeRegistrationException>(() => r.Register(Adapter(NodeKeys.Coding, Capabilities.CodingTest)));
        Assert.Contains("already registered", ex.Message);
    }

    [Fact]
    public void TwoNodesClaimingTheSameCapability_IsRejected_SoRoutingIsNeverAmbiguous()
    {
        var r = Registry();
        r.Register(Adapter(NodeKeys.Coding, Capabilities.CodingWrite));
        var ex = Assert.Throws<NodeRegistrationException>(() => r.Register(Adapter("acme.coder", Capabilities.CodingWrite)));
        Assert.Contains(Capabilities.CodingWrite, ex.Message);
        Assert.Contains(NodeKeys.Coding, ex.Message);
    }

    // ── degradation (doc §5.5 step 5): the core never dies because a node died ──

    [Fact]
    public void Degraded_RemovesCapabilitiesFromRouting_ThenRestores()
    {
        var r = Registry();
        r.Register(Adapter(NodeKeys.Knowledge, Capabilities.KnowledgeAnswer, Capabilities.KnowledgeGapFill));

        Assert.True(r.SetDegraded(NodeKeys.Knowledge, true));
        Assert.Null(r.Resolve(Capabilities.KnowledgeAnswer));
        Assert.Null(r.ResolveNode(NodeKeys.Knowledge));
        Assert.Empty(r.RoutableCapabilities);
        Assert.Single(r.Registrations);                 // still known, just not routable

        Assert.True(r.SetDegraded(NodeKeys.Knowledge, false));
        Assert.NotNull(r.Resolve(Capabilities.KnowledgeAnswer));
        Assert.Equal(2, r.RoutableCapabilities.Count);
    }

    [Fact]
    public void SetDegraded_OnUnknownNode_ReturnsFalse()
        => Assert.False(Registry().SetDegraded("darci.ghost", true));

    [Fact]
    public void Registration_CapturesManifestShaAsTheAuditAnchor()
    {
        var r = Registry();
        var adapter = Adapter(NodeKeys.Innovation, Capabilities.InnovationSynthesize);
        var reg = r.Register(adapter);

        Assert.Equal(adapter.Manifest.ComputeSha256(), reg.ManifestSha256);
        Assert.NotEqual(default, reg.RegisteredAt);
        Assert.False(reg.Degraded);
    }
}
