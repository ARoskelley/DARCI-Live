using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// SU6 — the full C1 payoff. A packet can now REQUEST a capability by string verb, so an external
/// collaborator's node is routable end-to-end (create → route → dispatch → persist → reload) with no enum
/// member and no core recompile. Also pins strict one-owner capability ownership.
/// </summary>
public sealed class StringCapabilityRoutingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteNodePacketStore _store;

    public StringCapabilityRoutingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-strcap-{Guid.NewGuid():N}.db");
        _store = new SqliteNodePacketStore($"Data Source={_dbPath}", NullLogger<SqliteNodePacketStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    /// <summary>An "external" node: it has no meaningful legacy enum identity, only manifest strings.</summary>
    private sealed class ExternalNode : INode
    {
        public bool WasCalled;
        public ExternalNode(NodeId id) => Id = id;
        public NodeId Id { get; }
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability>();
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(packet
                .Transition(Id, NodeState.Accepted, "accepted")
                .Transition(Id, NodeState.Working, "working")
                .Transition(Id, NodeState.Succeeded, "simulated", success: true));
        }
    }

    private (NodeRouter Router, ExternalNode Node) ExternalRouter(string nodeId, string capability)
    {
        var node = new ExternalNode(NodeId.Engineering);
        var manifest = new NodeManifest
        {
            NodeId = nodeId,
            DisplayName = nodeId,
            NodeVersion = "1.0.0",
            Endpoint = null,
            Capabilities = new[] { new NodeCapabilityDescriptor { Name = capability, Description = "external" } },
        };
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        registry.Register(new LegacyPacketNodeAdapter(node, manifest));
        var router = new NodeRouter(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance), _store,
            NullLogger<NodeRouter>.Instance);
        return (router, node);
    }

    [Fact]
    public async Task PacketRequestingAStringCapability_RoutesToAnExternalNode_WithNoEnumMember()
    {
        var (router, node) = ExternalRouter("acme.thermal", "acme.simulate_thermal");

        // No enum capability at all — only the string verb.
        var packet = NodePacket.Create("simulate the bracket", capabilityKey: "acme.simulate_thermal");
        Assert.Null(packet.RequestedCapability);
        Assert.Equal("acme.simulate_thermal", packet.EffectiveCapabilityKey);
        Assert.Null(CapabilityKey.ToLegacy("acme.simulate_thermal"));

        var result = await router.DispatchAsync(packet);

        Assert.True(node.WasCalled);
        Assert.Equal(NodeState.Succeeded, result.State);
    }

    [Fact]
    public async Task AStringOnlyCapability_SurvivesPersistenceAndReload()
    {
        // The ordinal columns cannot hold this verb, so if the string columns were not read back the packet
        // would silently become unroutable after a restart.
        var (router, _) = ExternalRouter("acme.thermal", "acme.simulate_thermal");
        var packet = NodePacket.Create("simulate", capabilityKey: "acme.simulate_thermal", addressKey: "acme.thermal");
        var result = await router.DispatchAsync(packet);

        var reloaded = await _store.GetPacketAsync(result.Id);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.RequestedCapability);                                  // no enum equivalent
        Assert.Equal("acme.simulate_thermal", reloaded.RequestedCapabilityKey);      // preserved
        Assert.Equal("acme.thermal", reloaded.AddressKey);
        Assert.Equal("acme.simulate_thermal", reloaded.EffectiveCapabilityKey);
    }

    [Fact]
    public void EnumAndStringForms_AlwaysAgreeWhenCreatedFromTheEnum()
    {
        var packet = NodePacket.Create("x", address: NodeId.Coding, capability: Capability.WriteCode);

        Assert.Equal(Capabilities.CodingWrite, packet.RequestedCapabilityKey);
        Assert.Equal(NodeKeys.Coding, packet.AddressKey);
        Assert.Equal(Capabilities.CodingWrite, packet.EffectiveCapabilityKey);
        Assert.Equal(NodeKeys.Coding, packet.EffectiveAddressKey);
    }

    [Fact]
    public void NoCapabilityAtAll_LeavesBothFormsNull()
    {
        var packet = NodePacket.Create("x");
        Assert.Null(packet.RequestedCapabilityKey);
        Assert.Null(packet.EffectiveCapabilityKey);
        Assert.Null(packet.EffectiveAddressKey);
    }

    // ── strict one-owner ownership (the resolved fork) ──

    [Fact]
    public void TwoNodesClaimingOneVerb_IsAlwaysFatal_AndSuggestsANamespacedAlternative()
    {
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var a = new ExternalNode(NodeId.Coding);
        var b = new ExternalNode(NodeId.Engineering);

        registry.Register(new LegacyPacketNodeAdapter(a, new NodeManifest
        {
            NodeId = NodeKeys.Coding, NodeVersion = "1.0.0",
            Capabilities = new[] { new NodeCapabilityDescriptor { Name = Capabilities.CodingWrite } },
        }));

        var ex = Assert.Throws<NodeRegistrationException>(() =>
            registry.Register(new LegacyPacketNodeAdapter(b, new NodeManifest
            {
                NodeId = NodeKeys.Engineering, NodeVersion = "1.0.0",
                Capabilities = new[] { new NodeCapabilityDescriptor { Name = Capabilities.CodingWrite } },
            })));

        Assert.Contains(Capabilities.CodingWrite, ex.Message);
        Assert.Contains("already served by", ex.Message);
        Assert.Contains("own namespaced verb", ex.Message);   // points at the fix
    }

    [Fact]
    public void TheLegacyConvenienceConstructor_IsAlsoStrict()
    {
        // Two packet-native nodes declaring the same enum capability is now a hard error, not first-wins.
        var one = new FakeEnumNode(NodeId.Coding, Capability.WriteCode);
        var two = new FakeEnumNode(NodeId.Engineering, Capability.WriteCode);

        Assert.Throws<NodeRegistrationException>(() =>
            new NodeRouter(new INode[] { one, two }, _store, NullLogger<NodeRouter>.Instance));
    }

    private sealed class FakeEnumNode : INode
    {
        public FakeEnumNode(NodeId id, params Capability[] caps)
        {
            Id = id;
            Capabilities = new HashSet<Capability>(caps);
        }
        public NodeId Id { get; }
        public IReadOnlySet<Capability> Capabilities { get; }
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
            => Task.FromResult(packet);
    }
}
