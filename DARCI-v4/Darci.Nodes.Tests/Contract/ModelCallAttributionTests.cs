using System.Net;
using System.Text;
using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// P2b.2 — attributing model calls to the invocation that caused them (fork F1a: ambient AsyncLocal scope).
/// The point is that ~57 call sites deep in node code need no changes, yet their model calls still land on
/// the right invocation.
/// </summary>
public sealed class ModelCallAttributionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteTelemetryStore _store;

    public ModelCallAttributionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-attrib-{Guid.NewGuid():N}.db");
        _store = new SqliteTelemetryStore($"Data Source={_dbPath}", NullLogger<SqliteTelemetryStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private sealed class CollectingCallSink : IModelCallSink
    {
        public List<ModelCallRecord> Calls { get; } = new();
        public void Record(ModelCallRecord call) => Calls.Add(call);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    private const string GenerateJson =
        """{"response":"ok","done":true,"prompt_eval_count":10,"eval_count":20}""";

    private static HostProfile Profile() => HostProfileLoader.FromEnvironment();

    private static (ModelBroker Broker, CollectingCallSink Sink) Broker()
    {
        var sink = new CollectingCallSink();
        var provider = new OllamaModelProvider(new HttpClient(new StubHandler(GenerateJson)), NullLogger<OllamaModelProvider>.Instance);
        var broker = new ModelBroker(Profile(), new IModelProvider[] { provider }, NullLogger<ModelBroker>.Instance, sink);
        return (broker, sink);
    }

    // ── the ambient scope ──

    [Fact]
    public async Task ModelCallsInsideAScope_AreAttributedToThatInvocation()
    {
        var (broker, sink) = Broker();

        using (ModelCallScope.Begin("trace-abc", "goal-xyz"))
        {
            await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p") { Purpose = "test" });
            await broker.EmbedAsync(new EmbeddingRequest("t"));
        }

        Assert.Equal(2, sink.Calls.Count);
        Assert.All(sink.Calls, c => Assert.Equal("trace-abc", c.TraceId));
        Assert.All(sink.Calls, c => Assert.Equal("goal-xyz", c.GoalId));
        Assert.Equal("test", sink.Calls[0].Purpose);
    }

    [Fact]
    public async Task ModelCallsOutsideAnyScope_AreUnattributed_NotMisattributed()
    {
        // The named limitation of the AsyncLocal approach: work outside an invocation (the autonomous loop,
        // fire-and-forget) has no scope. Recording it with EMPTY ids is honest; pinning it to whatever
        // invocation happened to run last would silently corrupt the analysis this data exists for.
        var (broker, sink) = Broker();

        await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));

        var call = Assert.Single(sink.Calls);
        Assert.Equal("", call.TraceId);
        Assert.Equal("", call.GoalId);
    }

    [Fact]
    public async Task ScopeIsRestoredAfterDisposal_SoCallsDoNotLeakIntoTheNextInvocation()
    {
        var (broker, sink) = Broker();

        using (ModelCallScope.Begin("trace-1", "goal-1"))
            await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));

        await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));   // outside

        Assert.Equal("trace-1", sink.Calls[0].TraceId);
        Assert.Equal("", sink.Calls[1].TraceId);
        Assert.Null(ModelCallScope.CurrentInvocation);
    }

    [Fact]
    public async Task NestedScopes_AttributeToTheInnermost_ThenRestoreTheOuter()
    {
        // A node routing a child packet nests invocations; the child's model calls belong to the child.
        var (broker, sink) = Broker();

        using (ModelCallScope.Begin("outer", "goal-1"))
        {
            await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));

            using (ModelCallScope.Begin("inner", "goal-1"))
                await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));

            await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));
        }

        Assert.Equal(new[] { "outer", "inner", "outer" }, sink.Calls.Select(c => c.TraceId).ToArray());
    }

    [Fact]
    public async Task AttributionSurvivesAwaits_WhichIsWhyAsyncLocalWasChosen()
    {
        var (broker, sink) = Broker();

        using (ModelCallScope.Begin("trace-deep", "goal-deep"))
            await ThreeFramesDown(broker);

        Assert.Equal("trace-deep", Assert.Single(sink.Calls).TraceId);

        static async Task ThreeFramesDown(IModelBroker b)
        {
            await Task.Yield();
            await Task.Delay(1);
            await b.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));
        }
    }

    // ── roll-up onto the invocation record, via the dispatcher ──

    private sealed class ModelCallingNode : INode
    {
        private readonly IModelBroker _broker;
        private readonly int _calls;
        public ModelCallingNode(IModelBroker broker, int calls) { _broker = broker; _calls = calls; }
        public NodeId Id => NodeId.Innovation;
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.Innovate };

        public async Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
        {
            for (var i = 0; i < _calls; i++)
                await _broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, $"p{i}"), ct);

            return packet
                .Transition(Id, NodeState.Accepted, "a")
                .Transition(Id, NodeState.Working, "w")
                .Transition(Id, NodeState.Succeeded, "done", success: true);
        }
    }

    private sealed class RecordingSink : INodeTelemetrySink
    {
        public List<NodeTelemetryRecord> Records { get; } = new();
        public void Record(NodeTelemetryRecord record) => Records.Add(record);
    }

    [Fact]
    public async Task Dispatcher_RollsUpTokensAndCallCount_OntoTheInvocationRecord()
    {
        var (broker, _) = Broker();
        var node = new ModelCallingNode(broker, calls: 3);
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var reg = registry.Register(LegacyPacketNodeAdapter.ForLegacyNode(node));

        var telemetry = new RecordingSink();
        var dispatcher = new NodeDispatcher(NullLogger<NodeDispatcher>.Instance, telemetry, Profile());

        var packet = NodePacket.Create("do it", capability: Capability.Innovate)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");
        await dispatcher.DispatchAsync(reg, packet, Capabilities.InnovationSynthesize);

        var record = Assert.Single(telemetry.Records);
        Assert.Equal(3, record.ModelCallCount);
        Assert.Equal(30, record.TokensIn);       // 3 calls x 10 prompt tokens
        Assert.Equal(60, record.TokensOut);      // 3 calls x 20 completion tokens
        Assert.Equal(ModelClasses.ChatBalanced, record.ModelClass);
        Assert.Equal(Profile().Resolve(ModelClasses.ChatBalanced)!.Model, record.ModelResolved);
        Assert.Equal(Profile().ProfileId, record.HostProfileId);
    }

    [Fact]
    public async Task AnInvocationThatCallsNoModel_ReportsNullModelFacts()
    {
        var node = new ModelCallingNode(Broker().Broker, calls: 0);
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var reg = registry.Register(LegacyPacketNodeAdapter.ForLegacyNode(node));

        var telemetry = new RecordingSink();
        var dispatcher = new NodeDispatcher(NullLogger<NodeDispatcher>.Instance, telemetry, Profile());

        await dispatcher.DispatchAsync(reg,
            NodePacket.Create("x", capability: Capability.Innovate).Transition(NodeId.Orchestrator, NodeState.Routed, "r"),
            Capabilities.InnovationSynthesize);

        var record = Assert.Single(telemetry.Records);
        Assert.Null(record.ModelCallCount);
        Assert.Null(record.TokensIn);
        Assert.Null(record.ModelClass);
    }

    // ── persistence of the per-call grain ──

    [Fact]
    public async Task ModelCalls_PersistAndAreRetrievableByTrace()
    {
        await _store.RecordModelCallAsync(new ModelCallRecord(
            "trace-1", "goal-1", ModelClasses.CodeGenerate, "qwen2.5-coder:7b", "ollama",
            DateTime.UtcNow, 850, 100, 250, true, "coding.Coding"));
        await _store.RecordModelCallAsync(new ModelCallRecord(
            "trace-1", "goal-1", ModelClasses.EmbedText, "nomic-embed-text", "ollama",
            DateTime.UtcNow, 20, 7, 0, true, "coding.embed"));
        await _store.RecordModelCallAsync(new ModelCallRecord(
            "trace-2", "goal-1", ModelClasses.ChatFast, "gemma2:9b", "ollama",
            DateTime.UtcNow, 10, 1, 1, false, null, "boom"));

        var forTrace1 = await _store.GetModelCallsAsync("trace-1");
        Assert.Equal(2, forTrace1.Count);
        Assert.Equal("qwen2.5-coder:7b", forTrace1[0].ResolvedModel);
        Assert.Equal(250, forTrace1[0].TokensOut);
        Assert.Equal("coding.Coding", forTrace1[0].Purpose);

        var forTrace2 = Assert.Single(await _store.GetModelCallsAsync("trace-2"));
        Assert.False(forTrace2.Succeeded);
        Assert.Equal("boom", forTrace2.Error);
    }

    [Fact]
    public async Task FailedModelCalls_AreStillRecorded_SoFailureRatesAreVisible()
    {
        var sink = new CollectingCallSink();
        var provider = new OllamaModelProvider(
            new HttpClient(new ThrowingHandler()), NullLogger<OllamaModelProvider>.Instance);
        var broker = new ModelBroker(Profile(), new IModelProvider[] { provider }, NullLogger<ModelBroker>.Instance, sink);

        using (ModelCallScope.Begin("t", "g"))
            await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));

        var call = Assert.Single(sink.Calls);
        Assert.False(call.Succeeded);
        Assert.NotNull(call.Error);
        Assert.Equal("t", call.TraceId);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
