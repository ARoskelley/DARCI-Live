#nullable enable

using System.Net;
using System.Text;
using System.Text.Json;
using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// M3 — the path POST /nodes/invoke actually takes: NodeRouter → NodeDispatcher → HttpNodeAdapter → a
/// node in another process, and back.
///
/// <para>The endpoint itself is a thin pass-through, and this repo has no API-level test harness, so
/// testing it through Kestrel would mean booting a core that reaches for Neo4j, Ollama and the real
/// nodes/ directory at startup. These tests sit one layer down instead, where all of the behaviour that
/// could actually be wrong lives, and they use a real HTTP server over a real socket rather than a
/// handler stub — a node in another process is the thing being modelled.</para>
///
/// <para>No Python required, so this runs in CI on a machine with no CAD toolchain.</para>
/// </summary>
public sealed class RemoteDispatchIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteNodePacketStore _store;
    private readonly StubRemoteNode _node = new();

    public RemoteDispatchIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-remote-{Guid.NewGuid():N}.db");
        _store = new SqliteNodePacketStore($"Data Source={_dbPath}", NullLogger<SqliteNodePacketStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _node.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private const string Capability = "model.build";
    private const string NodeId = "darci.modeling";

    private NodeManifest Manifest(string? endpoint) => new()
    {
        NodeId = NodeId,
        DisplayName = "3D Modeling",
        NodeVersion = "0.1.0",
        Endpoint = endpoint,
        Capabilities = new[]
        {
            new NodeCapabilityDescriptor { Name = Capability, Description = "build", DeadlineMs = 10_000 },
        },
    };

    private NodeRouter Router(string? endpoint, string? sharedSecret = null)
    {
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        if (endpoint is not null)
        {
            registry.Register(new HttpNodeAdapter(
                Manifest(endpoint), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance, sharedSecret));
        }

        return new NodeRouter(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance), _store,
            NullLogger<NodeRouter>.Instance);
    }

    private static NodePacket Packet(IReadOnlyDictionary<string, string>? slots = null) => new()
    {
        CorrelationId = Guid.NewGuid().ToString("N"),
        RequestedCapabilityKey = Capability,
        Payload = new PacketPayload
        {
            Intent = "build a part",
            Slots = slots ?? new Dictionary<string, string> { ["spec"] = """{"shape":"box"}""" },
        },
    };

    // ── the happy path ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACapabilityKeyedPacket_ReachesTheRemoteNode_AndComesBackSucceeded()
    {
        _node.InvokeResponse = (200, """
            {"outcome":"ok","payload":{"step_path":"C:/out/part.step","watertight":"true"}}
            """);

        var result = await Router(_node.BaseUrl).DispatchAsync(Packet());

        Assert.Equal(NodeState.Succeeded, result.State);
        // The node's slots are merged onto the work record — this is what the caller actually reads back.
        Assert.Equal("C:/out/part.step", result.Payload.Slot("step_path"));
        Assert.Equal("true", result.Payload.Slot("watertight"));
    }

    [Fact]
    public async Task TheRequestCarriesTheEnvelopeTheContractPromises()
    {
        var correlationRoot = Guid.NewGuid().ToString("N");
        var packet = Packet() with { CorrelationId = correlationRoot };

        await Router(_node.BaseUrl).DispatchAsync(packet);

        using var sent = JsonDocument.Parse(_node.LastInvokeBody!);
        var root = sent.RootElement;

        Assert.Equal(Capability, root.GetProperty("capability").GetString());
        // ADD-2: goal_id is the correlation ROOT, the durable key the evidence loop counts by; trace_id is
        // per-invocation and must NOT be the same value.
        Assert.Equal(correlationRoot, root.GetProperty("goal_id").GetString());
        Assert.NotEqual(correlationRoot, root.GetProperty("trace_id").GetString());
        Assert.Equal("""{"shape":"box"}""", root.GetProperty("payload").GetProperty("spec").GetString());
        Assert.True(root.TryGetProperty("deadline_at", out _));
    }

    [Fact]
    public async Task TheSharedSecretIsSentAsXDarciToken_WhenConfigured()
    {
        await Router(_node.BaseUrl, sharedSecret: "s3cret").DispatchAsync(Packet());

        Assert.Equal("s3cret", _node.LastToken);
    }

    // ── the negatives (M3) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenNoNodeServesTheCapability_ThePacketIsBlocked_NotFailed()
    {
        // The Phase 3 guarantee, and the one the /nodes/invoke caller inherits: a core running without
        // this node answers honestly instead of recording a failure nobody caused. Calling it Failed
        // would feed phantom negative evidence into the confidence and campaign paths.
        var result = await Router(endpoint: null).DispatchAsync(Packet());

        Assert.Equal(NodeState.Blocked, result.State);
        Assert.True(result.State.IsTerminal());
        Assert.Contains("model.build", result.Log[^1].Error);
    }

    [Fact]
    public async Task WhenTheNodeIsNotRunning_TheDispatchFails_ButTheCoreDoesNot()
    {
        // Registered (the node was up at discovery) and then gone. An unreachable node is a failed
        // dispatch, distinct from "nothing serves this" above.
        var deadEndpoint = $"http://127.0.0.1:{FreePort()}";

        var result = await Router(deadEndpoint).DispatchAsync(Packet());

        Assert.Equal(NodeState.Failed, result.State);
        Assert.Contains("unreachable", result.Log[^1].Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenTheNodeOverrunsItsDeadline_TheCoreAbandonsTheCall()
    {
        // The deadline is BINDING and enforced core-side, because an unresponsive node is exactly the
        // case this has to survive. The capability declares 10s; the stub takes longer than the adapter
        // will wait once the deadline is shortened to something testable.
        _node.InvokeDelay = TimeSpan.FromSeconds(5);
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var manifest = Manifest(_node.BaseUrl) with
        {
            Capabilities = new[]
            {
                new NodeCapabilityDescriptor { Name = Capability, Description = "slow", DeadlineMs = 300 },
            },
        };
        registry.Register(new HttpNodeAdapter(manifest, new HttpClient(), NullLogger<HttpNodeAdapter>.Instance));
        var router = new NodeRouter(registry, new NodeDispatcher(NullLogger<NodeDispatcher>.Instance), _store,
            NullLogger<NodeRouter>.Instance);

        var started = DateTime.UtcNow;
        var result = await router.DispatchAsync(Packet());
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(NodeState.Failed, result.State);
        // It gave up on its own schedule rather than waiting out the node.
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"waited {elapsed.TotalSeconds:0.0}s for a 0.3s deadline");
    }

    [Fact]
    public async Task ANodeReportingAnErrorOutcome_FailsThePacket_WithItsOwnMessage()
    {
        _node.InvokeResponse = (200, """
            {"outcome":"error","error":{"code":"INVALID_INPUT","message":"spec.shape is required","retryable":false}}
            """);

        var result = await Router(_node.BaseUrl).DispatchAsync(Packet());

        Assert.Equal(NodeState.Failed, result.State);
        Assert.Contains("spec.shape is required", result.Log[^1].Error);
    }

    [Fact]
    public async Task ANodeReportingBlocked_ParksTheWork_RatherThanFailingIt()
    {
        _node.InvokeResponse = (200, """
            {"outcome":"blocked","dependency":{"kind":"missing-environment","detail":"no CAD kernel on this host"}}
            """);

        var result = await Router(_node.BaseUrl).DispatchAsync(Packet());

        // AwaitingDependency, NOT Failed: the node did bounded work and reported honestly that it needs
        // something it does not have. Recording that as a failure is what would poison the evidence loop.
        Assert.Equal(NodeState.AwaitingDependency, result.State);
        Assert.Contains("no CAD kernel on this host", result.Log[^1].Decision);
    }

    [Fact]
    public async Task TheTwoKindsOfBlocked_AreDifferentStates_OnPurpose()
    {
        // Worth pinning because the words collide. A node saying "blocked" has been reached and has
        // parked its work on a dependency that may yet be satisfied — AwaitingDependency, non-terminal.
        // The ROUTER saying "blocked" means nothing serves the capability at all, which no amount of
        // waiting fixes at runtime — NodeState.Blocked, terminal, and it writes a durable gap.
        _node.InvokeResponse = (200, """{"outcome":"blocked","dependency":{"kind":"pending-outcome","detail":"waiting"}}""");

        var parked = await Router(_node.BaseUrl).DispatchAsync(Packet());
        var unserved = await Router(endpoint: null).DispatchAsync(Packet());

        Assert.Equal(NodeState.AwaitingDependency, parked.State);
        Assert.False(parked.State.IsTerminal());

        Assert.Equal(NodeState.Blocked, unserved.State);
        Assert.True(unserved.State.IsTerminal());
    }

    [Fact]
    public async Task ANodeSpeakingAnUnknownDialect_IsNotTreatedAsSuccess()
    {
        // "Anything unrecognised is an error, not an optimistic Ok" — a node whose vocabulary we do not
        // understand has not demonstrated anything.
        _node.InvokeResponse = (200, """{"outcome":"probably-fine"}""");

        var result = await Router(_node.BaseUrl).DispatchAsync(Packet());

        Assert.Equal(NodeState.Failed, result.State);
    }

    [Fact]
    public async Task AGarbageResponseBody_FailsCleanly()
    {
        _node.InvokeResponse = (200, "<html>not json</html>");

        var result = await Router(_node.BaseUrl).DispatchAsync(Packet());

        Assert.Equal(NodeState.Failed, result.State);
    }

    [Fact]
    public async Task AnHttpErrorFromTheNode_IsReportedAsDependencyUnavailable()
    {
        // What a node refusing the shared secret looks like from the core's side.
        _node.InvokeResponse = (401, """{"error":"X-Darci-Token missing or incorrect"}""");

        var result = await Router(_node.BaseUrl).DispatchAsync(Packet());

        Assert.Equal(NodeState.Failed, result.State);
        Assert.Contains("401", result.Log[^1].Error);
    }

    [Fact]
    public async Task EveryOutcome_LeavesADurablePacket_ThatCanBeReadBack()
    {
        // Persisted before anything is attempted, so a crash mid-dispatch still leaves a record.
        _node.InvokeResponse = (200, """{"outcome":"ok","payload":{"stl_path":"C:/out/part.stl"}}""");

        var result = await Router(_node.BaseUrl).DispatchAsync(Packet());
        var reloaded = await _store.GetPacketAsync(result.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(NodeState.Succeeded, reloaded!.State);
        Assert.Equal("C:/out/part.stl", reloaded.Payload.Slot("stl_path"));
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A node in another process, as far as the core can tell: a real socket speaking §5.2.</summary>
    private sealed class StubRemoteNode : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public string BaseUrl { get; }
        public (int Status, string Body) InvokeResponse = (200, """{"outcome":"ok"}""");
        public TimeSpan InvokeDelay = TimeSpan.Zero;
        public string? LastInvokeBody;
        public string? LastToken;

        public StubRemoteNode()
        {
            var port = FreePort();
            BaseUrl = $"http://localhost:{port}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                try { await HandleAsync(ctx); }
                catch { /* the stub must never take the test host down */ }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var (status, body) = (200, """{"status":"ok"}""");

            if (path.EndsWith("/invoke", StringComparison.Ordinal))
            {
                LastToken = ctx.Request.Headers["X-Darci-Token"];
                using (var reader = new StreamReader(ctx.Request.InputStream))
                    LastInvokeBody = await reader.ReadToEndAsync();

                if (InvokeDelay > TimeSpan.Zero) await Task.Delay(InvokeDelay);
                (status, body) = InvokeResponse;
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* best effort */ }
            _cts.Dispose();
        }
    }
}
