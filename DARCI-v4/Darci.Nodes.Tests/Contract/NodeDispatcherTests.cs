using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>SU3 — the one dispatch point: projection, invocation, fold, telemetry.</summary>
public sealed class NodeDispatcherTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteNodePacketStore _store;

    public NodeDispatcherTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-disp-{Guid.NewGuid():N}.db");
        _store = new SqliteNodePacketStore($"Data Source={_dbPath}", NullLogger<SqliteNodePacketStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private sealed class RecordingSink : INodeTelemetrySink
    {
        public List<NodeTelemetryRecord> Records { get; } = new();
        public void Record(NodeTelemetryRecord record) => Records.Add(record);
    }

    /// <summary>A legacy packet-native node whose terminal state is configurable.</summary>
    private sealed class LegacyNode : INode
    {
        private readonly NodeState _terminal;
        public NodeInvocation? Seen;   // set by the capturing adapter, not by the node itself
        public LegacyNode(NodeId id, NodeState terminal, params Capability[] caps)
        {
            Id = id;
            _terminal = terminal;
            Capabilities = new HashSet<Capability>(caps);
        }
        public NodeId Id { get; }
        public IReadOnlySet<Capability> Capabilities { get; }

        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
        {
            var p = packet
                .Transition(Id, NodeState.Accepted, "accepted")
                .Transition(Id, NodeState.Working, "working", leaseFor: TimeSpan.FromMinutes(5));

            p = _terminal switch
            {
                NodeState.Succeeded => p.Transition(Id, NodeState.Succeeded, "done", confidence: Confidence.Of(0.75), success: true),
                NodeState.Failed => p.Transition(Id, NodeState.Failed, "broke", success: false, error: "BOOM"),
                NodeState.AwaitingDependency => p.ParkAwaitingDependency(Id, "awaiting human approval"),
                _ => p,
            };
            return Task.FromResult(p);
        }
    }

    /// <summary>Wraps the legacy adapter to capture the invocation the dispatcher projected.</summary>
    private sealed class CapturingAdapter : INodeAdapter
    {
        private readonly LegacyPacketNodeAdapter _inner;
        public NodeInvocation? Captured;
        public CapturingAdapter(LegacyPacketNodeAdapter inner) => _inner = inner;
        public NodeManifest Manifest => _inner.Manifest;
        public Task<NodeResult> InvokeAsync(NodeInvocation invocation, CancellationToken ct = default)
        {
            Captured = invocation;
            return _inner.InvokeAsync(invocation, ct);
        }
    }

    private static (NodeRegistry Registry, CapturingAdapter Adapter) RegistryFor(INode node)
    {
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var adapter = new CapturingAdapter(LegacyPacketNodeAdapter.ForLegacyNode(node));
        registry.Register(adapter);
        return (registry, adapter);
    }

    private NodeRouter RouterFor(INodeRegistry registry, NodeDispatcher dispatcher) =>
        new(registry, dispatcher, _store, NullLogger<NodeRouter>.Instance);

    // ── ADD-2: the projection must key correlation off the packet's correlation root ──

    [Fact]
    public async Task Projection_MapsCorrelationRootToGoalId_AndMintsAFreshTraceId()
    {
        var node = new LegacyNode(NodeId.Coding, NodeState.Succeeded, Capability.WriteCode);
        var (registry, adapter) = RegistryFor(node);
        var sink = new RecordingSink();
        var router = RouterFor(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance, sink));

        var packet = NodePacket.Create("do it", capability: Capability.WriteCode);
        var root = packet.CorrelationId;
        await router.DispatchAsync(packet);

        var inv = adapter.Captured!;
        Assert.Equal(root, inv.GoalId);                 // ← goal_id IS the correlation root
        Assert.NotEqual(root, inv.TraceId);             // ← trace_id is NOT the correlation key
        Assert.Equal(Capabilities.CodingWrite, inv.Capability);
        Assert.Equal("do it", inv.Intent);
        Assert.NotNull(inv.PacketRef);                  // F1a in-process side-channel supplied

        // Telemetry carries both ids, so a trace can be tied back to its goal.
        var t = Assert.Single(sink.Records);
        Assert.Equal(root, t.GoalId);
        Assert.Equal(inv.TraceId, t.TraceId);
        Assert.Equal(NodeKeys.Coding, t.NodeId);
        Assert.Equal(Capabilities.CodingWrite, t.Capability);
        Assert.Equal(NodeOutcome.Ok, t.Outcome);
        Assert.Equal(0.75, t.Confidence.Score, 4);
    }

    [Fact]
    public async Task Projection_DeadlineComesFromTheManifest_NotTheLease()
    {
        var node = new LegacyNode(NodeId.Innovation, NodeState.Succeeded, Capability.Innovate);
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var manifest = LegacyPacketNodeAdapter.SynthesizeManifest(node) with
        {
            Capabilities = new[]
            {
                new NodeCapabilityDescriptor { Name = Capabilities.InnovationSynthesize, DeadlineMs = 12_345 },
            },
        };
        var adapter = new CapturingAdapter(new LegacyPacketNodeAdapter(node, manifest));
        registry.Register(adapter);

        var router = RouterFor(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance));
        var before = DateTime.UtcNow;
        await router.DispatchAsync(NodePacket.Create("x", capability: Capability.Innovate));

        var inv = adapter.Captured!;
        var budget = inv.DeadlineAt - before;
        Assert.InRange(budget.TotalMilliseconds, 12_000, 20_000);   // manifest's 12.345s, not a lease
    }

    // ── outcome classification from what the node did to the record ──

    [Fact]
    public async Task LegacyNode_Succeeded_MapsToOk_AndThePacketPassesThroughUnchanged()
    {
        var node = new LegacyNode(NodeId.Coding, NodeState.Succeeded, Capability.WriteCode);
        var (registry, _) = RegistryFor(node);
        var sink = new RecordingSink();
        var router = RouterFor(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance, sink));

        var result = await router.DispatchAsync(NodePacket.Create("x", capability: Capability.WriteCode));

        Assert.Equal(NodeState.Succeeded, result.State);
        // The node's own log entries survive verbatim — that is what makes the carve behavior-preserving.
        Assert.Equal(new[] { NodeState.Routed, NodeState.Accepted, NodeState.Working, NodeState.Succeeded },
            result.Log.Select(l => l.StateAfter).ToArray());
        Assert.Equal(NodeOutcome.Ok, Assert.Single(sink.Records).Outcome);
    }

    [Fact]
    public async Task LegacyNode_Failed_MapsToErrorOutcome_ButKeepsTheNodesOwnFailureLog()
    {
        var node = new LegacyNode(NodeId.Coding, NodeState.Failed, Capability.WriteCode);
        var (registry, _) = RegistryFor(node);
        var sink = new RecordingSink();
        var router = RouterFor(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance, sink));

        var result = await router.DispatchAsync(NodePacket.Create("x", capability: Capability.WriteCode));

        Assert.Equal(NodeState.Failed, result.State);
        Assert.Equal("BOOM", result.LastEntry!.Error);
        var t = Assert.Single(sink.Records);
        Assert.Equal(NodeOutcome.Error, t.Outcome);
        Assert.Equal("INTERNAL", t.ErrorCode);
    }

    [Fact]
    public async Task LegacyNode_ThatParked_MapsToBlocked_NotAnError_AndStaysParked()
    {
        // Rev 0.1.1: a node that parked the record is BLOCKED on a dependency — not a failure, not a retry.
        var node = new LegacyNode(NodeId.Innovation, NodeState.AwaitingDependency, Capability.Innovate);
        var (registry, _) = RegistryFor(node);
        var sink = new RecordingSink();
        var router = RouterFor(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance, sink));

        var result = await router.DispatchAsync(NodePacket.Create("x", capability: Capability.Innovate));

        Assert.Equal(NodeState.AwaitingDependency, result.State);
        Assert.Null(result.LeaseExpiresAt);                       // parking cleared the lease
        var t = Assert.Single(sink.Records);
        Assert.Equal(NodeOutcome.Blocked, t.Outcome);
        Assert.Equal(DependencyKind.HumanDecision, t.BlockedOn);
        Assert.Null(t.ErrorCode);                                 // blocked is not an error
    }

    // ── the C1 payoff, end to end through the router ──

    [Fact]
    public async Task Router_RoutesAStringOnlyCapability_ThroughTheRegistry()
    {
        // A node registered by MANIFEST with a capability that has no `Capability` enum member. It is
        // addressed by node id, because the legacy NodePacket can only express enum capabilities — which is
        // exactly why SU5/SU6 migrate the packet's own fields to strings.
        var node = new LegacyNode(NodeId.Engineering, NodeState.Succeeded);
        var manifest = LegacyPacketNodeAdapter.SynthesizeManifest(node) with
        {
            Capabilities = new[] { new NodeCapabilityDescriptor { Name = "acme.simulate_thermal" } },
        };
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        registry.Register(new LegacyPacketNodeAdapter(node, manifest));

        var sink = new RecordingSink();
        var router = RouterFor(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance, sink));

        var result = await router.DispatchAsync(NodePacket.Create("simulate", address: NodeId.Engineering));

        Assert.Equal(NodeState.Succeeded, result.State);
        Assert.Equal("acme.simulate_thermal", Assert.Single(sink.Records).Capability);
    }

    [Fact]
    public async Task Router_DegradedNode_IsNotRouted_AndFailsWithTheUsualNamedError()
    {
        var node = new LegacyNode(NodeId.Coding, NodeState.Succeeded, Capability.WriteCode);
        var (registry, _) = RegistryFor(node);
        registry.SetDegraded(NodeKeys.Coding, true);

        var router = RouterFor(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance));
        var result = await router.DispatchAsync(NodePacket.Create("x", capability: Capability.WriteCode));

        Assert.Equal(NodeState.Failed, result.State);
        Assert.Contains("No node", result.LastEntry!.Decision);
    }

    [Fact]
    public async Task LegacyAdapter_RefusesAPayloadOnlyInvocation_UntilTheNodeIsDeLegacied()
    {
        var node = new LegacyNode(NodeId.Coding, NodeState.Succeeded, Capability.WriteCode);
        var adapter = LegacyPacketNodeAdapter.ForLegacyNode(node);

        // No PacketRef ⇒ the transitional side-channel is absent ⇒ this node cannot serve it yet.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.InvokeAsync(new NodeInvocation { GoalId = "g", Capability = Capabilities.CodingWrite }));
        Assert.Contains("de-legacied", ex.Message);
    }

    [Fact]
    public void SynthesizedManifest_ForALegacyNode_IsValid()
    {
        var node = new LegacyNode(NodeId.Knowledge, NodeState.Succeeded, Capability.AnswerKnowledge, Capability.FillKnowledgeGap);
        var manifest = LegacyPacketNodeAdapter.SynthesizeManifest(node);

        Assert.Empty(manifest.Validate());
        Assert.Equal(NodeKeys.Knowledge, manifest.NodeId);
        Assert.True(manifest.IsInProcess);
        Assert.Equal(new[] { Capabilities.KnowledgeAnswer, Capabilities.KnowledgeGapFill },
            manifest.Capabilities.Select(c => c.Name).ToArray());
    }
}
