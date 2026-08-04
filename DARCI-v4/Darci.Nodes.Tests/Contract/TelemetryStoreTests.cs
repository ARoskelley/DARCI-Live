using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>P2b.1 — durable telemetry in its own database, written without blocking the dispatch path.</summary>
public sealed class TelemetryStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteTelemetryStore _store;

    public TelemetryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-telemetry-{Guid.NewGuid():N}.db");
        _store = new SqliteTelemetryStore($"Data Source={_dbPath}", NullLogger<SqliteTelemetryStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static NodeTelemetryRecord Sample(string goal = "goal-1", string trace = "trace-1") =>
        new(trace, goal, NodeKeys.Innovation, Capabilities.InnovationSynthesize,
            DateTime.UtcNow, 1234, NodeOutcome.Ok, Confidence.Of(0.42));

    [Fact]
    public async Task RoundTripsAnInvocationRecord()
    {
        await _store.RecordInvocationAsync(Sample());

        var got = Assert.Single(await _store.GetRecentAsync());
        Assert.Equal("trace-1", got.TraceId);
        Assert.Equal("goal-1", got.GoalId);
        Assert.Equal(NodeKeys.Innovation, got.NodeId);
        Assert.Equal(Capabilities.InnovationSynthesize, got.Capability);
        Assert.Equal(1234, got.DurationMs);
        Assert.Equal(NodeOutcome.Ok, got.Outcome);
        Assert.Equal(0.42, got.Confidence.Score, 4);
    }

    [Fact]
    public async Task RoundTripsTheModelRollUpFields()
    {
        await _store.RecordInvocationAsync(Sample() with
        {
            ModelClass = ModelClasses.ChatDeep,
            ModelResolved = "gemma2:9b",
            TokensIn = 1204,
            TokensOut = 310,
            ModelCallCount = 3,
            HostProfileId = "tinman-3070ti-local",
        });

        var got = Assert.Single(await _store.GetRecentAsync());
        Assert.Equal(ModelClasses.ChatDeep, got.ModelClass);
        Assert.Equal("gemma2:9b", got.ModelResolved);
        Assert.Equal(1204, got.TokensIn);
        Assert.Equal(310, got.TokensOut);
        Assert.Equal(3, got.ModelCallCount);
        Assert.Equal("tinman-3070ti-local", got.HostProfileId);
    }

    [Fact]
    public async Task AnInvocationThatCalledNoModel_HasNullModelFacts_NotZeros()
    {
        // "made no model calls" and "used zero tokens" are different claims; the schema must not conflate them.
        await _store.RecordInvocationAsync(Sample());
        var got = Assert.Single(await _store.GetRecentAsync());
        Assert.Null(got.ModelClass);
        Assert.Null(got.TokensIn);
        Assert.Null(got.ModelCallCount);
    }

    [Fact]
    public async Task RoundTripsErrorAndBlockedOutcomes()
    {
        await _store.RecordInvocationAsync(Sample(trace: "t-err") with
        {
            Outcome = NodeOutcome.Error, ErrorCode = "DEADLINE_EXCEEDED",
        });
        await _store.RecordInvocationAsync(Sample(trace: "t-blocked") with
        {
            Outcome = NodeOutcome.Blocked, BlockedOn = DependencyKind.HumanDecision, TaintLevel = TaintLevel.Derived,
        });

        var all = await _store.GetRecentAsync();
        var err = all.Single(r => r.TraceId == "t-err");
        Assert.Equal(NodeOutcome.Error, err.Outcome);
        Assert.Equal("DEADLINE_EXCEEDED", err.ErrorCode);

        var blocked = all.Single(r => r.TraceId == "t-blocked");
        Assert.Equal(NodeOutcome.Blocked, blocked.Outcome);
        Assert.Equal(DependencyKind.HumanDecision, blocked.BlockedOn);
        Assert.Equal(TaintLevel.Derived, blocked.TaintLevel);
        Assert.Null(blocked.ErrorCode);
    }

    [Fact]
    public async Task GetByGoal_ReturnsTheWholeCausalChainInOrder()
    {
        // The point of goal_id: one goal's invocations across several nodes are retrievable together.
        await _store.RecordInvocationAsync(Sample(goal: "G1", trace: "a") with { NodeId = NodeKeys.Coding });
        await _store.RecordInvocationAsync(Sample(goal: "G1", trace: "b") with { NodeId = NodeKeys.Knowledge });
        await _store.RecordInvocationAsync(Sample(goal: "G2", trace: "c"));

        var chain = await _store.GetByGoalAsync("G1");
        Assert.Equal(new[] { "a", "b" }, chain.Select(r => r.TraceId).ToArray());
        Assert.Single(await _store.GetByGoalAsync("G2"));
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();
        await _store.RecordInvocationAsync(Sample());
        Assert.Single(await _store.GetRecentAsync());
    }

    // ── the sink: never blocks, never takes the app down ──

    [Fact]
    public async Task Sink_WritesThroughToTheStore()
    {
        await using var sink = new TelemetryStoreSink(_store, NullLogger<TelemetryStoreSink>.Instance);

        sink.Record(Sample(trace: "queued-1"));
        sink.Record(Sample(trace: "queued-2"));
        await sink.DisposeAsync();   // flushes

        var all = await _store.GetRecentAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.TraceId == "queued-1");
        Assert.Contains(all, r => r.TraceId == "queued-2");
    }

    [Fact]
    public async Task Sink_DropsRatherThanBlocking_WhenSaturated()
    {
        // Telemetry must never stall the dispatch path. A tiny queue + a burst proves the overflow path
        // drops and counts instead of blocking or throwing.
        var slow = new BlockingStore();
        await using var sink = new TelemetryStoreSink(slow, NullLogger<TelemetryStoreSink>.Instance, capacity: 4);

        for (var i = 0; i < 500; i++) sink.Record(Sample(trace: $"burst-{i}"));   // must return promptly

        Assert.True(sink.Dropped > 0, "a saturated queue should drop records rather than block");
        slow.Release();
    }

    [Fact]
    public void Sink_RecordNeverThrows_EvenIfTheStoreIsBroken()
    {
        var sink = new TelemetryStoreSink(new ThrowingStore(), NullLogger<TelemetryStoreSink>.Instance);
        var ex = Record.Exception(() => sink.Record(Sample()));
        Assert.Null(ex);   // a telemetry failure must never surface into the work path
    }

    [Fact]
    public void CompositeSink_FansOutToEverySink()
    {
        var a = new CountingSink();
        var b = new CountingSink();
        var composite = new CompositeNodeTelemetrySink(new INodeTelemetrySink[] { a, b });

        composite.Record(Sample());

        Assert.Equal(1, a.Count);
        Assert.Equal(1, b.Count);
    }

    private sealed class CountingSink : INodeTelemetrySink
    {
        public int Count;
        public void Record(NodeTelemetryRecord record) => Count++;
    }

    private sealed class ThrowingStore : ITelemetryStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordInvocationAsync(NodeTelemetryRecord record, CancellationToken ct = default)
            => throw new InvalidOperationException("disk on fire");
        public Task<IReadOnlyList<NodeTelemetryRecord>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NodeTelemetryRecord>>(Array.Empty<NodeTelemetryRecord>());
        public Task<IReadOnlyList<NodeTelemetryRecord>> GetByGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NodeTelemetryRecord>>(Array.Empty<NodeTelemetryRecord>());
    }

    private sealed class BlockingStore : ITelemetryStore
    {
        private readonly SemaphoreSlim _gate = new(0);
        public void Release() => _gate.Release(int.MaxValue / 2);
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async Task RecordInvocationAsync(NodeTelemetryRecord record, CancellationToken ct = default)
            => await _gate.WaitAsync(ct);
        public Task<IReadOnlyList<NodeTelemetryRecord>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NodeTelemetryRecord>>(Array.Empty<NodeTelemetryRecord>());
        public Task<IReadOnlyList<NodeTelemetryRecord>> GetByGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NodeTelemetryRecord>>(Array.Empty<NodeTelemetryRecord>());
    }
}
