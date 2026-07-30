using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// SU4 — THE C3 FIREWALL. The doc's node contract is a stateless invocation with a binding per-call
/// <c>deadline_at</c> (its own example uses 30 seconds). DARCI's human gate legitimately parks a durable work
/// record for DAYS, survives restarts, and resumes on an operator decision. Collapsing those two ideas would
/// break the gate — so these tests pin the separation:
///
/// <list type="number">
/// <item>parking is a property of the WORK RECORD (core-side), never of an invocation;</item>
/// <item>an invocation deadline can never shorten, extend, or abort the record's lease;</item>
/// <item>the dispatcher never parks, unparks, or reaps anything.</item>
/// </list>
///
/// A future refactor that quietly makes the dispatcher lifecycle-aware will fail here.
/// </summary>
public sealed class ParkingFirewallTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteNodePacketStore _store;

    public ParkingFirewallTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-firewall-{Guid.NewGuid():N}.db");
        _store = new SqliteNodePacketStore($"Data Source={_dbPath}", NullLogger<SqliteNodePacketStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    /// <summary>A node that returns the record exactly as handed to it — so anything that changes is the
    /// dispatcher's doing, not the node's.</summary>
    private sealed class PassiveNode : INode
    {
        public int Calls;
        public NodeId Id => NodeId.Innovation;
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.Innovate };
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(packet);
        }
    }

    private static (NodeRegistry Registry, NodeRegistration Reg) RegistryWith(INode node, int deadlineMs = 300_000)
    {
        var manifest = LegacyPacketNodeAdapter.SynthesizeManifest(node) with
        {
            Capabilities = new[] { new NodeCapabilityDescriptor { Name = Capabilities.InnovationSynthesize, DeadlineMs = deadlineMs } },
        };
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var reg = registry.Register(new LegacyPacketNodeAdapter(node, manifest));
        return (registry, reg);
    }

    private static NodeDispatcher Dispatcher() => new(NullLogger<NodeDispatcher>.Instance);

    private static NodePacket ParkedPacket() =>
        NodePacket.Create("await a human", capability: Capability.Innovate)
            .Transition(NodeId.Innovation, NodeState.Routed, "r")
            .Transition(NodeId.Innovation, NodeState.Accepted, "a")
            .Transition(NodeId.Innovation, NodeState.Working, "w", leaseFor: TimeSpan.FromMinutes(5))
            .ParkAwaitingDependency(NodeId.Innovation, "awaiting human approval");

    // ── 1. the dispatcher never unparks a record ──

    [Fact]
    public async Task Dispatcher_DoesNotUnparkAParkedRecord_AndLeavesTheLeaseNull()
    {
        var node = new PassiveNode();
        var (_, reg) = RegistryWith(node);
        var parked = ParkedPacket();
        Assert.Equal(NodeState.AwaitingDependency, parked.State);
        Assert.Null(parked.LeaseExpiresAt);

        var result = await Dispatcher().DispatchAsync(reg, parked, Capabilities.InnovationSynthesize);

        Assert.Equal(NodeState.AwaitingDependency, result.State);   // still parked
        Assert.Null(result.LeaseExpiresAt);                          // still no lease held
        Assert.Equal(parked.Log.Count, result.Log.Count);            // dispatcher appended nothing
    }

    // ── 2. the per-invocation deadline is independent of the lease ──

    [Fact]
    public void Deadline_IsIndependentOfTheLease_EvenWhenTheRecordHasNone()
    {
        // A parked record has a NULL lease. Naively "clamping the deadline to the remaining lease" would
        // produce an already-expired deadline and make every invocation instantly time out.
        var (_, reg) = RegistryWith(new PassiveNode(), deadlineMs: 60_000);
        var parked = ParkedPacket();

        var inv = NodeDispatcher.Project(parked, reg, Capabilities.InnovationSynthesize);

        Assert.Null(parked.LeaseExpiresAt);
        Assert.False(inv.IsExpired(DateTime.UtcNow));
        Assert.InRange(inv.DeadlineAt - DateTime.UtcNow, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Deadline_DoesNotInheritALongLease()
    {
        // Converse direction: a 10-hour lease must not grant a 10-hour invocation budget.
        var (_, reg) = RegistryWith(new PassiveNode(), deadlineMs: 5_000);
        var working = NodePacket.Create("long job", capability: Capability.Innovate)
            .Transition(NodeId.Innovation, NodeState.Routed, "r")
            .Transition(NodeId.Innovation, NodeState.Accepted, "a")
            .Transition(NodeId.Innovation, NodeState.Working, "w", leaseFor: TimeSpan.FromHours(10));

        var inv = NodeDispatcher.Project(working, reg, Capabilities.InnovationSynthesize);

        Assert.True(working.LeaseExpiresAt > DateTime.UtcNow.AddHours(9));
        Assert.True(inv.DeadlineAt < DateTime.UtcNow.AddMinutes(1));   // bounded by the manifest, not the lease
    }

    [Fact]
    public async Task AnExpiredInvocationDeadline_DoesNotAbortOrTouchTheRecord()
    {
        // deadline_ms of 1 → the invocation budget is already gone. The record must be untouched: whether a
        // deadline breach means anything for the GOAL is the core's decision, not the dispatcher's.
        var node = new PassiveNode();
        var (_, reg) = RegistryWith(node, deadlineMs: 1);
        var working = NodePacket.Create("x", capability: Capability.Innovate)
            .Transition(NodeId.Innovation, NodeState.Routed, "r")
            .Transition(NodeId.Innovation, NodeState.Accepted, "a")
            .Transition(NodeId.Innovation, NodeState.Working, "w", leaseFor: TimeSpan.FromMinutes(5));

        var result = await Dispatcher().DispatchAsync(reg, working, Capabilities.InnovationSynthesize);

        Assert.NotEqual(NodeState.Aborted, result.State);
        Assert.Equal(NodeState.Working, result.State);
        Assert.NotNull(result.LeaseExpiresAt);   // the lease is the watchdog's business, untouched here
    }

    // ── 3. parked + a live proposal is still invisible to the watchdog (post-carve) ──

    [Fact]
    public async Task ParkedRecord_WithALivePendingProposal_SurvivesTheStartupSweep_AfterTheCarve()
    {
        var proposals = new SqliteProposalStore($"Data Source={_dbPath}", NullLogger<SqliteProposalStore>.Instance);
        await proposals.InitializeAsync();

        var parked = ParkedPacket();
        await _store.CreatePacketAsync(parked);
        await proposals.AddAsync(new HumanProposal
        {
            CorrelationId = parked.CorrelationId,
            Kind = HumanProposalKind.AuthorizeCampaign,
            SubjectId = "campaign-1",
            Title = "Authorize",
            ParkedPacketId = parked.Id,
        });

        var watchdog = new NodeWatchdog(_store, NullLogger<NodeWatchdog>.Instance, proposals);
        Assert.Equal(0, await watchdog.SweepStartupOrphansAsync());
        Assert.Equal(0, await watchdog.SweepExpiredLeasesAsync(DateTime.UtcNow.AddYears(1)));

        var after = await _store.GetPacketAsync(parked.Id);
        Assert.Equal(NodeState.AwaitingDependency, after!.State);   // a days-long human wait is safe
    }

    // ── 4. a node that parks is BLOCKED, and that is not a failure ──

    [Fact]
    public async Task NodeThatParks_ReportsBlocked_WithoutTheRecordBecomingFailed()
    {
        var parking = new ParkingNode();
        var manifest = LegacyPacketNodeAdapter.SynthesizeManifest(parking) with
        {
            Capabilities = new[] { new NodeCapabilityDescriptor { Name = Capabilities.InnovationSynthesize } },
        };
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var reg = registry.Register(new LegacyPacketNodeAdapter(parking, manifest));

        var sink = new CollectingSink();
        var result = await new NodeDispatcher(NullLogger<NodeDispatcher>.Instance, sink)
            .DispatchAsync(reg, WorkingPacket(), Capabilities.InnovationSynthesize);

        Assert.Equal(NodeState.AwaitingDependency, result.State);
        Assert.Null(result.LeaseExpiresAt);
        var t = Assert.Single(sink.Records);
        Assert.Equal(NodeOutcome.Blocked, t.Outcome);
        Assert.Null(t.ErrorCode);   // NOT an error — the goal is waiting, nothing failed
    }

    private static NodePacket WorkingPacket() =>
        NodePacket.Create("x", capability: Capability.Innovate)
            .Transition(NodeId.Innovation, NodeState.Routed, "r")
            .Transition(NodeId.Innovation, NodeState.Accepted, "a")
            .Transition(NodeId.Innovation, NodeState.Working, "w", leaseFor: TimeSpan.FromMinutes(5));

    private sealed class ParkingNode : INode
    {
        public NodeId Id => NodeId.Innovation;
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.Innovate };
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
            => Task.FromResult(packet.ParkAwaitingDependency(NodeId.Innovation, "awaiting human authorization"));
    }

    private sealed class CollectingSink : INodeTelemetrySink
    {
        public List<NodeTelemetryRecord> Records { get; } = new();
        public void Record(NodeTelemetryRecord record) => Records.Add(record);
    }
}
