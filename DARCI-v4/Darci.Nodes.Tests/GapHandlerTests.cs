using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

public sealed class GapHandlerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteGapStore _store;

    public GapHandlerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-gaphandler-{Guid.NewGuid():N}.db");
        _store = new SqliteGapStore($"Data Source={_dbPath}", NullLogger<SqliteGapStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // Router that records dispatches and returns a Succeeded packet.
    private sealed class RecordingRouter : INodeRouter
    {
        public NodePacket? Dispatched;
        public Task<NodePacket> DispatchAsync(NodePacket packet, CancellationToken ct = default)
        {
            Dispatched = packet;
            var done = packet
                .Transition(NodeId.Orchestrator, NodeState.Routed, "routed")
                .Transition(NodeId.Knowledge, NodeState.Accepted, "a")
                .Transition(NodeId.Knowledge, NodeState.Working, "w")
                .Transition(NodeId.Knowledge, NodeState.Succeeded, "done", success: true);
            return Task.FromResult(done);
        }
    }

    private sealed class RecordingGoalSink : IGapGoalSink
    {
        public readonly List<GapRecord> Created = new();
        public Task<string?> CreateGoalForGapAsync(GapRecord gap, CancellationToken ct = default)
        {
            Created.Add(gap);
            return Task.FromResult<string?>($"goal-{Created.Count}");
        }
    }

    private GapHandler Handler(RecordingRouter router, IGapGoalSink? sink, int maxDepth = 1) =>
        new(_store, new Lazy<INodeRouter>(() => router), new GapHandlerOptions { MaxImmediateFillDepth = maxDepth },
            NullLogger<GapHandler>.Instance, sink);

    private static NodePacket WorkingPacket(int depth = 0)
    {
        var slots = new Dictionary<string, string>();
        if (depth > 0) slots[PacketSlots.GapFillDepth] = depth.ToString();
        return NodePacket.Create("implement a Damm checksum", capability: Capability.FillKnowledgeGap, slots: slots)
            .Transition(NodeId.Knowledge, NodeState.Routed, "r")
            .Transition(NodeId.Knowledge, NodeState.Accepted, "a")
            .Transition(NodeId.Knowledge, NodeState.Working, "w");
    }

    private static GapContext Ctx(bool blocking) =>
        new("What is the Damm table?", "implement a Damm checksum",
            new[] { "the exact quasigroup table" }, Confidence.Of(0.3), blocking, NodeId.Knowledge);

    [Fact]
    public async Task BlockingGapOnCriticalPath_ImmediateFill_RoutesAndLogs()
    {
        var router = new RecordingRouter();
        var handler = Handler(router, sink: new RecordingGoalSink());

        var outcome = await handler.HandleAsync(WorkingPacket(depth: 0), Ctx(blocking: true));

        Assert.Equal(GapDisposition.Immediate, outcome.Disposition);
        Assert.NotNull(router.Dispatched);                              // routed a fill packet
        Assert.Equal("1", router.Dispatched!.Payload.Slot(PacketSlots.GapFillDepth)); // depth incremented (recursion guard)
        Assert.Equal("true", router.Dispatched.Payload.Slot(PacketSlots.Blocking));
        Assert.NotNull(outcome.FillResult);

        // Decision logged on the origin packet.
        Assert.Contains(outcome.Packet.Log, e => e.Decision.Contains("IMMEDIATE", StringComparison.OrdinalIgnoreCase));

        // Gap persisted as "filling".
        var filling = await _store.GetByStatusAsync(GapStatus.Filling);
        Assert.Single(filling);
    }

    [Fact]
    public async Task NonBlockingGap_Deferred_PersistsAndCreatesTaggedAutoGoal()
    {
        var router = new RecordingRouter();
        var sink = new RecordingGoalSink();
        var handler = Handler(router, sink);

        var outcome = await handler.HandleAsync(WorkingPacket(depth: 0), Ctx(blocking: false));

        Assert.Equal(GapDisposition.Deferred, outcome.Disposition);
        Assert.Null(router.Dispatched);                                // no immediate fill
        Assert.Single(sink.Created);                                   // an auto-goal was requested

        // The gap handed to the sink retains full context for traceability / future ideation node.
        var handed = sink.Created[0];
        Assert.Equal("implement a Damm checksum", handed.Intent);
        Assert.Equal("the exact quasigroup table", handed.Missing);
        Assert.False(string.IsNullOrEmpty(handed.CorrelationId));

        // Persisted with the goal link + status.
        var withGoals = await _store.GetByStatusAsync(GapStatus.GoalCreated);
        Assert.Single(withGoals);
        Assert.Equal("goal-1", withGoals[0].GoalId);

        // Decision logged.
        Assert.Contains(outcome.Packet.Log, e => e.Decision.Contains("DEFERRED", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BlockingButFillBudgetExhausted_FallsBackToDeferred()
    {
        var router = new RecordingRouter();
        var sink = new RecordingGoalSink();
        var handler = Handler(router, sink, maxDepth: 1);

        // depth already at the max → no more immediate fills, defer instead.
        var outcome = await handler.HandleAsync(WorkingPacket(depth: 1), Ctx(blocking: true));

        Assert.Equal(GapDisposition.Deferred, outcome.Disposition);
        Assert.Null(router.Dispatched);
        Assert.Single(sink.Created);
    }

    [Fact]
    public async Task DeferredWithoutSink_StillPersistsGap()
    {
        var router = new RecordingRouter();
        var handler = Handler(router, sink: null);

        var outcome = await handler.HandleAsync(WorkingPacket(depth: 0), Ctx(blocking: false));

        Assert.Equal(GapDisposition.Deferred, outcome.Disposition);
        var deferred = await _store.GetByStatusAsync(GapStatus.Deferred);
        Assert.Single(deferred);                                       // persisted even with no goal sink
        Assert.Null(deferred[0].GoalId);
    }

    [Fact]
    public async Task NoGaps_IsNoOp()
    {
        var router = new RecordingRouter();
        var handler = Handler(router, new RecordingGoalSink());
        var ctx = new GapContext("q", "i", Array.Empty<string>(), Confidence.Unassessed, true, NodeId.Knowledge);

        var outcome = await handler.HandleAsync(WorkingPacket(), ctx);

        Assert.Equal(GapDisposition.None, outcome.Disposition);
        Assert.Null(router.Dispatched);
    }
}
