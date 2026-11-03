#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// SU 3.0 — THE DEGRADATION ORACLE. Phase 3's governing principle, in Tinman's words: nodes are
/// "detected in startup and worked into the loop, instead of they're critical to have in the loop for it
/// to work. That way people can have a running core without the nodes that I've built."
///
/// <para>So the invariant these tests exist to protect is: <b>no node is a hard dependency</b>. A core with
/// ZERO nodes, or any SUBSET of them, must run and answer honestly. A capability nobody serves must produce
/// a clean, well-formed outcome — never a crash, a hang, or an NRE.</para>
///
/// <para>This suite landed BEFORE the Phase 3 changes so it could catch regressions in them. One test was
/// written to pin behavior we intended to change and is now marked
/// <c>*** RE-BLESSED IN SU 3.1, NOT A REGRESSION ***</c>: an unservable capability reported
/// <c>Failed</c> and now reports <c>Blocked</c>. Inverting a marked test is a deliberate, reviewed act;
/// inverting an UNMARKED one is a regression and must be treated as one.</para>
///
/// <para>The three "terminal" assertions did NOT need re-blessing, which is the point of Option 3:
/// <see cref="NodeState.Blocked"/> is a THIRD TERMINAL state, so degraded packets stay terminal and never
/// become orphan-sweep targets.</para>
///
/// <para>The evidence-loop guardrails at the bottom are the non-negotiable half. Fork 1 moves
/// "no node serves this" from <c>Failed</c> to <c>blocked</c>/<c>missing-environment</c>, and the one thing
/// that change must never do is manufacture or mis-resolve evidence.</para>
/// </summary>
public sealed class DegradationOracleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteNodePacketStore _packets;
    private readonly string _gapDbPath;
    private readonly string _innovatedDbPath;
    private readonly SqliteInnovatedKnowledgeStore _innovated;

    public DegradationOracleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-degrade-{Guid.NewGuid():N}.db");
        _packets = new SqliteNodePacketStore($"Data Source={_dbPath}", NullLogger<SqliteNodePacketStore>.Instance);
        _packets.InitializeAsync().GetAwaiter().GetResult();

        _gapDbPath = Path.Combine(Path.GetTempPath(), $"darci-degrade-gap-{Guid.NewGuid():N}.db");
        _innovatedDbPath = Path.Combine(Path.GetTempPath(), $"darci-degrade-inv-{Guid.NewGuid():N}.db");
        _innovated = new SqliteInnovatedKnowledgeStore(
            $"Data Source={_innovatedDbPath}", NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        _innovated.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var p in new[] { _dbPath, _innovatedDbPath, _gapDbPath })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

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
            return Task.FromResult(packet
                .Transition(Id, NodeState.Accepted, "accepted")
                .Transition(Id, NodeState.Working, "working")
                .Transition(Id, NodeState.Succeeded, "done", success: true));
        }
    }

    private NodeRouter Router(params INode[] nodes) =>
        NodeRouter.ForNodes(nodes, _packets, NullLogger<NodeRouter>.Instance);

    // ══ A core with ZERO nodes ══

    [Fact]
    public void AnEmptyRegistry_IsAValidCore_NotAnError()
    {
        // Constructing a core with no nodes at all must be an ordinary, supported state.
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);

        Assert.Empty(registry.Registrations);
        Assert.Null(registry.Resolve(Capabilities.CodingWrite));
        Assert.Null(registry.Resolve(Capabilities.KnowledgeAnswer));
        Assert.Null(registry.Resolve(Capabilities.InnovationSynthesize));
        Assert.Null(registry.ResolveNode(NodeKeys.Coding));
    }

    [Fact]
    public void AZeroNodeRouter_CanBeConstructed()
    {
        // The router is not allowed to require that anything be registered.
        var router = Router();
        Assert.NotNull(router);
    }

    [Fact]
    public async Task AZeroNodeCore_DispatchingAnything_DoesNotThrow()
    {
        var router = Router();

        var packet = NodePacket.Create("write me some code", capability: Capability.WriteCode);
        var result = await router.DispatchAsync(packet);

        Assert.NotNull(result);
        Assert.True(result.State.IsTerminal(), "an unservable packet must reach a terminal state, not hang.");
    }

    [Fact]
    public async Task AZeroNodeCore_PersistsTheUnservablePacket()
    {
        // The core still owes a durable, inspectable record of what was asked and why nothing happened.
        var router = Router();

        var packet = NodePacket.Create("write me some code", capability: Capability.WriteCode);
        var result = await router.DispatchAsync(packet);

        var stored = await _packets.GetPacketAsync(result.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.State.IsTerminal());
    }

    [Fact]
    public async Task AZeroNodeCore_ExplainsWhichCapabilityWasUnavailable()
    {
        // An honest core says what it could not do. Without this the operator cannot tell a missing node
        // from a broken one.
        var router = Router();

        var packet = NodePacket.Create("fill a gap", capability: Capability.FillKnowledgeGap);
        var result = await router.DispatchAsync(packet);

        var text = (result.LastEntry?.Error ?? "") + " " + string.Join(" ", result.Log.Select(e => e.Decision));
        Assert.Contains("capability", text, StringComparison.OrdinalIgnoreCase);
    }

    // ══ a SUBSET of nodes ══

    [Fact]
    public async Task ASubsetCore_ServesWhatItHasAndDegradesTheRest()
    {
        // The realistic collaborator case: some of Tinman's nodes, not all of them.
        var coding = new FakeNode(NodeId.Coding, Capability.WriteCode);
        var router = Router(coding);

        var served = await router.DispatchAsync(
            NodePacket.Create("do coding", capability: Capability.WriteCode));
        var unserved = await router.DispatchAsync(
            NodePacket.Create("fill a gap", capability: Capability.FillKnowledgeGap));

        Assert.Equal(NodeState.Succeeded, served.State);
        Assert.True(coding.WasCalled);
        Assert.True(unserved.State.IsTerminal());
        Assert.NotEqual(NodeState.Succeeded, unserved.State);
    }

    [Fact]
    public void CapabilityAvailability_IsTheHonestPredicate_NotRouterPresence()
    {
        // The defect Phase 3 exists to remove: call sites branch on "is a router wired?" as a proxy for
        // "can anything actually do this?". A router with nodes in it still cannot serve a capability none
        // of them claims, and the registry has always been able to say so.
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        registry.Register(LegacyPacketNodeAdapter.ForLegacyNode(
            new FakeNode(NodeId.Coding, Capability.WriteCode)));

        Assert.NotNull(registry.Resolve(Capabilities.CodingWrite));
        Assert.Null(registry.Resolve(Capabilities.KnowledgeGapFill));
        Assert.Null(registry.Resolve(Capabilities.InnovationSynthesize));
    }

    // ══ *** RE-BLESSED IN SU 3.1, NOT A REGRESSION *** ══

    [Fact]
    public async Task AnUnservableCapability_IsBlocked_NotFailed()
    {
        // *** RE-BLESSED IN SU 3.1, NOT A REGRESSION ***
        // This test previously asserted Failed. Fork 1 (approved): "no node serves this" is not a failure,
        // it is a dependency the goal is blocked on, and Rev 0.1.1 added `blocked`/`missing-environment`
        // for exactly this. NodeState.Blocked was APPENDED at 8 as a THIRD terminal outcome — terminal so
        // the packet cannot leak as an active orphan, distinct so it is never counted as success or
        // failure. "Attempted and genuinely broke" still maps to Failed; that test is unmarked below.
        var router = Router();

        var result = await router.DispatchAsync(
            NodePacket.Create("write code", capability: Capability.WriteCode));

        Assert.Equal(NodeState.Blocked, result.State);
    }

    [Fact]
    public async Task Blocked_IsTerminal_SoItIsNeverAnOrphanSweepTarget()
    {
        // The reason Blocked is terminal rather than a parked AwaitingDependency: a packet waiting for a
        // node that cannot appear at runtime is a leak, and the watchdog's startup orphan sweep would
        // abort it at the next restart — a delayed, silent state change.
        var router = Router();

        var result = await router.DispatchAsync(
            NodePacket.Create("write code", capability: Capability.WriteCode));

        Assert.True(result.State.IsTerminal());
        Assert.False(result.State.IsActive());
    }

    [Fact]
    public async Task Blocked_IsNeitherSuccessNorFailure()
    {
        // The whole point of the third terminal state. Anything asking "did this go wrong" must get NO.
        var router = Router();

        var result = await router.DispatchAsync(
            NodePacket.Create("write code", capability: Capability.WriteCode));

        Assert.False(result.State.IsFailure());
        Assert.False(result.State.IsSuccess());
        Assert.NotEqual(true, result.LastEntry?.Success);
        Assert.NotEqual(false, result.LastEntry?.Success);
    }

    [Fact]
    public void TheStateMachine_TreatsBlockedAsTerminalAndUnleavable()
    {
        Assert.True(NodeState.Blocked.IsTerminal());
        Assert.False(NodeStateMachine.CanTransition(NodeState.Blocked, NodeState.Working));
        Assert.False(NodeStateMachine.CanTransition(NodeState.Blocked, NodeState.Succeeded));

        // Ordinal order is load-bearing: the store's "active" query is state <= AwaitingDependency (4).
        Assert.True((int)NodeState.Blocked > (int)NodeState.AwaitingDependency);
    }

    [Fact]
    public async Task AnUnservableCapability_RecordsADurableGap_TheRestartSafeHomeForTheNeed()
    {
        // The packet terminates, so the standing need must live somewhere that outlives it. This is what
        // makes a bare core honest: it can say "I need a node for coding.write" after a restart.
        var gaps = new SqliteGapStore($"Data Source={_gapDbPath}", NullLogger<SqliteGapStore>.Instance);
        await gaps.InitializeAsync();

        var router = new NodeRouter(
            new NodeRegistry(NullLogger<NodeRegistry>.Instance),
            new NodeDispatcher(NullLogger<NodeDispatcher>.Instance),
            _packets,
            NullLogger<NodeRouter>.Instance,
            gaps);

        var root = $"root-{Guid.NewGuid():N}";
        await router.DispatchAsync(NodePacket.Create(
            "write code", capability: Capability.WriteCode, correlationId: root));

        // Read back through a SEPARATE store instance — proving it is on disk, not in memory.
        var reopened = new SqliteGapStore($"Data Source={_gapDbPath}", NullLogger<SqliteGapStore>.Instance);
        var recorded = await reopened.GetByCorrelationAsync(root);

        var gap = Assert.Single(recorded);
        Assert.Contains(Capabilities.CodingWrite, gap.Missing);
        Assert.Equal(GapStatus.Open, gap.Status);
    }

    [Fact]
    public async Task AZeroNodeCore_WithNoGapStore_StillDegradesCleanly()
    {
        // A core with nowhere to write the note must still degrade, not throw. Recording the gap is
        // best-effort; the degradation is not.
        var router = Router();

        var result = await router.DispatchAsync(
            NodePacket.Create("write code", capability: Capability.WriteCode));

        Assert.Equal(NodeState.Blocked, result.State);
    }

    [Fact]
    public void CanServe_AnswersTheHonestQuestion()
    {
        var withNode = NodeRouter.ForNodes(
            new INode[] { new FakeNode(NodeId.Coding, Capability.WriteCode) },
            _packets, NullLogger<NodeRouter>.Instance);

        Assert.True(withNode.CanServe(Capabilities.CodingWrite));
        Assert.False(withNode.CanServe(Capabilities.KnowledgeGapFill));
        Assert.False(Router().CanServe(Capabilities.CodingWrite));
    }

    [Fact]
    public async Task ANodeThatActuallyBreaks_StillReportsFailed()
    {
        // NOT marked for re-blessing. This is the distinction Fork 1 depends on staying crisp: a node that
        // was reached and threw is a real failure, and must never be softened into "blocked" just because
        // the unavailable path now uses it.
        var router = Router(new ThrowingNode());

        var result = await router.DispatchAsync(
            NodePacket.Create("write code", capability: Capability.WriteCode));

        Assert.Equal(NodeState.Failed, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.LastEntry?.Error));
    }

    private sealed class ThrowingNode : INode
    {
        public NodeId Id => NodeId.Coding;
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.WriteCode };
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
            => throw new InvalidOperationException("the node genuinely broke");
    }

    // ══ EVIDENCE-LOOP GUARDRAILS (Fork 1 condition b) ══
    //
    // The evidence loop is the thing we protect hardest: innovated knowledge earns trust ONLY from real
    // consumption outcomes keyed to the correlation root. A capability that was never served did no work,
    // so it must leave that ledger untouched in every direction.

    [Fact]
    public async Task CapabilityUnavailable_WritesNoConsumptionLink()
    {
        var root = $"root-{Guid.NewGuid():N}";
        var router = Router();

        await router.DispatchAsync(NodePacket.Create(
            "synthesize something", capability: Capability.Innovate, correlationId: root));

        // Nothing was served, so nothing may claim to have been served into this root.
        var entries = await _innovated.GetEntriesByConsumptionRootAsync(root);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task CapabilityUnavailable_DoesNotResolveAnExistingConsumptionLink()
    {
        // The dangerous shape: an entry legitimately served into a goal, then a LATER unavailable-capability
        // dispatch on the SAME correlation root. If the degraded path were treated as an outcome, that entry
        // would gain or lose trust for work that never ran.
        var root = $"root-{Guid.NewGuid():N}";
        var entry = new InnovatedKnowledgeRecord
        {
            Hypothesis = "a capped hypothesis", Topic = "t", Intent = "i",
            Provenance = Provenance.Innovated, Confidence = Confidence.Of(0.3),
        };
        await _innovated.AddAsync(entry);
        await _innovated.RecordConsumptionAsync(entry.Id, root);

        var router = Router();
        await router.DispatchAsync(NodePacket.Create(
            "do coding", capability: Capability.WriteCode, correlationId: root));

        var consumptions = await _innovated.GetConsumptionsAsync(entry.Id);
        var link = Assert.Single(consumptions);
        Assert.Equal(ConsumptionOutcome.Pending, link.Outcome);
        Assert.Null(link.ResolvedAt);
    }

    [Fact]
    public async Task CapabilityUnavailable_LeavesDistinctOutcomeCountsUntouched()
    {
        var root = $"root-{Guid.NewGuid():N}";
        var entry = new InnovatedKnowledgeRecord
        {
            Hypothesis = "a capped hypothesis", Topic = "t", Intent = "i",
            Provenance = Provenance.Innovated, Confidence = Confidence.Of(0.3),
        };
        await _innovated.AddAsync(entry);
        await _innovated.RecordConsumptionAsync(entry.Id, root);

        var before = await _innovated.CountDistinctOutcomesAsync(entry.Id);
        var router = Router();
        await router.DispatchAsync(NodePacket.Create(
            "innovate", capability: Capability.Innovate, correlationId: root));
        var after = await _innovated.CountDistinctOutcomesAsync(entry.Id);

        Assert.Equal(before, after);
        Assert.Equal((0, 0), after);
    }

    [Fact]
    public async Task CapabilityUnavailable_DoesNotCreateInnovatedEntries()
    {
        // No phantom knowledge: a core with no innovation node must not end up with innovated records.
        var root = $"root-{Guid.NewGuid():N}";
        var router = Router();

        await router.DispatchAsync(NodePacket.Create(
            "synthesize", capability: Capability.Innovate, correlationId: root));

        Assert.Empty(await _innovated.GetByCorrelationAsync(root));
        Assert.Empty(await _innovated.GetByProvenanceAsync(Provenance.Innovated));
    }

    [Fact]
    public async Task CapabilityUnavailable_PreservesTheCorrelationRoot()
    {
        // The correlation root is the durable evidence key (ADD-2). A degraded dispatch must carry it
        // through unchanged, or later work under the same goal would not join up.
        var root = $"root-{Guid.NewGuid():N}";
        var router = Router();

        var result = await router.DispatchAsync(NodePacket.Create(
            "do coding", capability: Capability.WriteCode, correlationId: root));

        Assert.Equal(root, result.CorrelationId);
    }
}
