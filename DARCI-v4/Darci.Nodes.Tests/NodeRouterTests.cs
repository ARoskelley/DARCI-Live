using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

public sealed class NodeRouterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteNodePacketStore _store;

    public NodeRouterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-router-{Guid.NewGuid():N}.db");
        _store = new SqliteNodePacketStore($"Data Source={_dbPath}", NullLogger<SqliteNodePacketStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // A fake node that records that it was hit and drives the packet to Succeeded.
    private sealed class FakeNode : INode
    {
        public FakeNode(NodeId id, params Capability[] caps)
        {
            Id = id;
            Capabilities = new HashSet<Capability>(caps);
        }
        public NodeId Id { get; }
        public IReadOnlySet<Capability> Capabilities { get; }
        public bool WasCalled { get; private set; }

        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
        {
            WasCalled = true;
            var working = packet
                .Transition(Id, NodeState.Accepted, "accepted")
                .Transition(Id, NodeState.Working, "working")
                .Transition(Id, NodeState.Succeeded, "done", success: true);
            return Task.FromResult(working);
        }
    }

    private NodeRouter Router(params INode[] nodes) =>
        NodeRouter.ForNodes(nodes, _store, NullLogger<NodeRouter>.Instance);

    [Fact]
    public async Task Dispatch_ByExplicitAddress_HitsThatNode()
    {
        var coding = new FakeNode(NodeId.Coding, Capability.WriteCode);
        var knowledge = new FakeNode(NodeId.Knowledge, Capability.FillKnowledgeGap);
        var router = Router(coding, knowledge);

        var packet = NodePacket.Create("do coding", address: NodeId.Coding);
        var result = await router.DispatchAsync(packet);

        Assert.True(coding.WasCalled);
        Assert.False(knowledge.WasCalled);
        Assert.Equal(NodeState.Succeeded, result.State);
    }

    [Fact]
    public async Task Dispatch_ByCapability_ResolvesNode()
    {
        var coding = new FakeNode(NodeId.Coding, Capability.WriteCode);
        var knowledge = new FakeNode(NodeId.Knowledge, Capability.FillKnowledgeGap);
        var router = Router(coding, knowledge);

        var packet = NodePacket.Create("fill a gap", capability: Capability.FillKnowledgeGap);
        var result = await router.DispatchAsync(packet);

        Assert.True(knowledge.WasCalled);
        Assert.False(coding.WasCalled);
        Assert.Equal(NodeState.Succeeded, result.State);
    }

    [Fact]
    public async Task Dispatch_PersistsPacket_Retrievable()
    {
        var coding = new FakeNode(NodeId.Coding, Capability.WriteCode);
        var router = Router(coding);

        var packet = NodePacket.Create("do coding", address: NodeId.Coding);
        var result = await router.DispatchAsync(packet);

        var loaded = await _store.GetPacketAsync(result.Id);
        Assert.NotNull(loaded);
        Assert.Equal(NodeState.Succeeded, loaded!.State);
        // Routed entry (by orchestrator) + the node's accepted/working/done entries.
        Assert.Contains(loaded.Log, e => e.Node == NodeId.Orchestrator && e.StateAfter == NodeState.Routed);
    }

    [Fact]
    public async Task Dispatch_NoMatchingNode_FailsGracefully()
    {
        var knowledge = new FakeNode(NodeId.Knowledge, Capability.FillKnowledgeGap);
        var router = Router(knowledge);

        var packet = NodePacket.Create("do coding", capability: Capability.WriteCode);
        var result = await router.DispatchAsync(packet);

        Assert.Equal(NodeState.Failed, result.State);
        Assert.False(knowledge.WasCalled);
        Assert.Contains("No node", result.LastEntry!.Decision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dispatch_AddressTakesPrecedenceOverCapability()
    {
        // The requested capability is served by 'other', but the explicit address pins it to Coding.
        //
        // This previously had BOTH nodes declaring Capability.WriteCode and relied on the router silently
        // resolving that overlap first-wins by registration order. Capability ownership is now strict — one
        // owner per verb — so each node declares its own distinct capability. The intent under test is
        // unchanged (address beats capability) and is now expressed unambiguously.
        var coding = new FakeNode(NodeId.Coding, Capability.WriteCode);
        var other = new FakeNode(NodeId.Engineering, Capability.DesignGeometry);
        var router = Router(other, coding);

        var packet = NodePacket.Create("x", address: NodeId.Coding, capability: Capability.DesignGeometry);
        await router.DispatchAsync(packet);

        Assert.True(coding.WasCalled);    // address won
        Assert.False(other.WasCalled);    // ...even though 'other' owns the requested capability
    }
}
