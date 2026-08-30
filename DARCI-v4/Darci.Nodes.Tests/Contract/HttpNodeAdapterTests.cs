#nullable enable

using System.Net;
using System.Text;
using System.Text.Json;
using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// SU 3.3 — the out-of-process transport, exercised against a REAL HTTP server on a real socket rather than
/// a mocked message handler. The failures this adapter exists to survive are transport failures — a node
/// that is absent, slow, or answering wrongly — and a fake handler cannot produce them convincingly.
///
/// <para>Governing rule (doc §5.5): <b>the core never dies because a node died.</b> Every case below must
/// come back as a well-formed <see cref="NodeResult"/>, never an exception.</para>
/// </summary>
public sealed class HttpNodeAdapterTests : IDisposable
{
    private readonly StubNodeServer _server = new();

    public void Dispose() => _server.Dispose();

    private HttpNodeAdapter Adapter(string? secret = null, NodeManifest? manifest = null) =>
        new(manifest ?? ManifestFor(_server.BaseUrl), new HttpClient(),
            NullLogger<HttpNodeAdapter>.Instance, secret);

    private static NodeManifest ManifestFor(string endpoint) => new()
    {
        NodeId = "acme.summarize",
        DisplayName = "Acme Summarizer",
        NodeVersion = "1.0.0",
        Endpoint = endpoint,
        Health = "/health",
        Capabilities = new[]
        {
            new NodeCapabilityDescriptor { Name = "summarize.text", Description = "Summarize text." },
        },
    };

    private static NodeInvocation Invocation(int deadlineSeconds = 30) => new()
    {
        GoalId = "goal-root-1",
        Capability = "summarize.text",
        Intent = "summarize this",
        DeadlineAt = DateTime.UtcNow.AddSeconds(deadlineSeconds),
        Payload = new Dictionary<string, string> { ["question"] = "what is this" },
    };

    // ── health (§5.2) ──

    [Fact]
    public async Task Health_WhenTheNodeSaysOk_IsHealthy()
    {
        _server.HealthResponse = (200, """{"status":"ok"}""");
        Assert.True(await Adapter().IsHealthyAsync());
    }

    [Fact]
    public async Task Health_WhenTheNodeIsUpButNotReady_IsNotHealthy()
    {
        // §5.2 is specific: 200 with {"status":"ok"} means "able to serve". A 200 saying anything else is a
        // node reporting that it is running but NOT ready, which must not be registered as servable.
        _server.HealthResponse = (200, """{"status":"starting"}""");
        Assert.False(await Adapter().IsHealthyAsync());
    }

    [Fact]
    public async Task Health_OnAnErrorStatus_IsNotHealthy()
    {
        _server.HealthResponse = (503, """{"status":"ok"}""");
        Assert.False(await Adapter().IsHealthyAsync());
    }

    [Fact]
    public async Task Health_AgainstADeadEndpoint_IsFalseRatherThanThrowing()
    {
        var adapter = new HttpNodeAdapter(
            ManifestFor("http://127.0.0.1:1"), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        Assert.False(await adapter.IsHealthyAsync());
    }

    // ── manifest (§5.2) ──

    [Fact]
    public async Task FetchManifest_ReturnsWhatTheNodeReports()
    {
        _server.ManifestResponse = (200, JsonSerializer.Serialize(
            ManifestFor(_server.BaseUrl), ManifestJson.Options));

        var fetched = await Adapter().FetchManifestAsync();

        Assert.NotNull(fetched);
        Assert.Equal("acme.summarize", fetched!.NodeId);
        Assert.Contains(fetched.Capabilities, c => c.Name == "summarize.text");
    }

    [Fact]
    public async Task FetchManifest_WhenUnreachable_IsNullRatherThanThrowing()
    {
        var adapter = new HttpNodeAdapter(
            ManifestFor("http://127.0.0.1:1"), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        Assert.Null(await adapter.FetchManifestAsync());
    }

    // ── invoke: the happy path (§5.3) ──

    [Fact]
    public async Task Invoke_MapsAnOkResponse()
    {
        _server.InvokeResponse = (200, """
            {"envelope_version":"0.1.1","outcome":"ok","confidence":0.91,
             "payload":{"knowledge_findings":"the answer"}}
            """);

        var result = await Adapter().InvokeAsync(Invocation());

        Assert.Equal(NodeOutcome.Ok, result.Outcome);
        Assert.Equal("the answer", result.Payload["knowledge_findings"]);
        Assert.Equal(0.91, result.Confidence.Score, 3);
    }

    [Fact]
    public async Task Invoke_SendsTheDocumentedEnvelopeInSnakeCase()
    {
        _server.InvokeResponse = (200, """{"outcome":"ok"}""");
        var invocation = Invocation();

        await Adapter().InvokeAsync(invocation);

        var sent = JsonDocument.Parse(_server.LastInvokeBody!).RootElement;
        Assert.Equal(invocation.TraceId, sent.GetProperty("trace_id").GetString());
        Assert.Equal("goal-root-1", sent.GetProperty("goal_id").GetString());
        Assert.Equal("summarize.text", sent.GetProperty("capability").GetString());
        Assert.Equal("what is this", sent.GetProperty("payload").GetProperty("question").GetString());
        Assert.True(sent.TryGetProperty("deadline_at", out _));
    }

    [Fact]
    public async Task Invoke_EchoesOurTraceId_EvenIfTheNodeReturnsADifferentOne()
    {
        // §5.3 says trace_id MUST be echoed unchanged. A node that returns someone else's would scramble
        // telemetry correlation, so the core trusts its own.
        _server.InvokeResponse = (200, """{"outcome":"ok","trace_id":"a-completely-different-id"}""");
        var invocation = Invocation();

        var result = await Adapter().InvokeAsync(invocation);

        Assert.Equal(invocation.TraceId, result.TraceId);
    }

    [Fact]
    public async Task Invoke_OmittedConfidence_IsUnassessedNotZero()
    {
        // §5.3: "Omit rather than fabricate". Zero would be a claim of no confidence, which is different.
        _server.InvokeResponse = (200, """{"outcome":"ok"}""");

        var result = await Adapter().InvokeAsync(Invocation());

        Assert.False(result.Confidence.IsAssessed);
    }

    // ── invoke: blocked + error taxonomy (§5.4) ──

    [Fact]
    public async Task Invoke_MapsBlockedWithItsDependencyKind()
    {
        _server.InvokeResponse = (200, """
            {"outcome":"blocked",
             "dependency":{"kind":"missing-environment","detail":"no sandbox","reference_id":"c-1"}}
            """);

        var result = await Adapter().InvokeAsync(Invocation());

        Assert.Equal(NodeOutcome.Blocked, result.Outcome);
        Assert.Equal(DependencyKind.MissingEnvironment, result.Dependency!.Kind);
        Assert.Equal("c-1", result.Dependency.ReferenceId);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Invoke_MapsTheErrorTaxonomy()
    {
        _server.InvokeResponse = (200, """
            {"outcome":"error","error":{"code":"PERMISSION_DENIED","message":"nope","retryable":false}}
            """);

        var result = await Adapter().InvokeAsync(Invocation());

        Assert.Equal(NodeOutcome.Error, result.Outcome);
        Assert.Equal(NodeErrorCode.PermissionDenied, result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Null(result.Dependency);
    }

    [Fact]
    public async Task Invoke_AnUnrecognizedOutcome_IsAnErrorNotAnOptimisticOk()
    {
        // A node speaking a dialect we do not understand has not demonstrated success.
        _server.InvokeResponse = (200, """{"outcome":"probably-fine"}""");

        var result = await Adapter().InvokeAsync(Invocation());

        Assert.Equal(NodeOutcome.Error, result.Outcome);
    }

    // ── invoke: the failures that must never reach the core as exceptions ──

    [Fact]
    public async Task Invoke_AgainstADeadNode_ReturnsDependencyUnavailable()
    {
        var adapter = new HttpNodeAdapter(
            ManifestFor("http://127.0.0.1:1"), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        var result = await adapter.InvokeAsync(Invocation());

        Assert.Equal(NodeOutcome.Error, result.Outcome);
        Assert.Equal(NodeErrorCode.DependencyUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task Invoke_OnAnHttpErrorStatus_ReturnsDependencyUnavailable()
    {
        _server.InvokeResponse = (500, "boom");

        var result = await Adapter().InvokeAsync(Invocation());

        Assert.Equal(NodeOutcome.Error, result.Outcome);
        Assert.Equal(NodeErrorCode.DependencyUnavailable, result.Error!.Code);
        Assert.Contains("500", result.Error.Message);
    }

    [Fact]
    public async Task Invoke_OnGarbageJson_ReturnsAnError()
    {
        _server.InvokeResponse = (200, "not json at all");

        var result = await Adapter().InvokeAsync(Invocation());

        Assert.Equal(NodeOutcome.Error, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Invoke_WhenTheNodeIsSlow_AbandonsAtTheDeadline()
    {
        // §5.3: "The core will abandon the call at the deadline regardless." Enforced by the core rather
        // than trusted to the node, because an unresponsive node is exactly the case this must survive.
        _server.InvokeDelay = TimeSpan.FromSeconds(10);
        _server.InvokeResponse = (200, """{"outcome":"ok"}""");

        var started = DateTime.UtcNow;
        var result = await Adapter().InvokeAsync(Invocation(deadlineSeconds: 1));
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(NodeOutcome.Error, result.Outcome);
        Assert.Equal(NodeErrorCode.DeadlineExceeded, result.Error!.Code);
        Assert.True(elapsed < TimeSpan.FromSeconds(8), $"took {elapsed.TotalSeconds:0.#}s — the deadline was not enforced.");
    }

    [Fact]
    public async Task Invoke_WithAnAlreadyPassedDeadline_DoesNotEvenCallTheNode()
    {
        _server.InvokeResponse = (200, """{"outcome":"ok"}""");

        var result = await Adapter().InvokeAsync(Invocation(deadlineSeconds: -5));

        Assert.Equal(NodeErrorCode.DeadlineExceeded, result.Error!.Code);
        Assert.Null(_server.LastInvokeBody);
    }

    // ── D4: the minimal loopback secret ──

    [Fact]
    public async Task Invoke_SendsTheSharedSecretWhenConfigured()
    {
        _server.InvokeResponse = (200, """{"outcome":"ok"}""");

        await Adapter(secret: "s3cret").InvokeAsync(Invocation());

        Assert.Equal("s3cret", _server.LastToken);
    }

    [Fact]
    public async Task Invoke_SendsNoSecretHeaderWhenNoneIsConfigured()
    {
        _server.InvokeResponse = (200, """{"outcome":"ok"}""");

        await Adapter().InvokeAsync(Invocation());

        Assert.Null(_server.LastToken);
    }

    /// <summary>A real HTTP server speaking the §5.2 three-endpoint surface, so the adapter is exercised
    /// over an actual socket.</summary>
    private sealed class StubNodeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public string BaseUrl { get; }
        public (int Status, string Body) HealthResponse = (200, """{"status":"ok"}""");
        public (int Status, string Body) ManifestResponse = (200, "{}");
        public (int Status, string Body) InvokeResponse = (200, """{"outcome":"ok"}""");
        public TimeSpan InvokeDelay = TimeSpan.Zero;
        public string? LastInvokeBody;
        public string? LastToken;

        public StubNodeServer()
        {
            var port = FreePort();
            BaseUrl = $"http://localhost:{port}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
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
            (int Status, string Body) reply;

            if (path.EndsWith("/health", StringComparison.Ordinal))
            {
                reply = HealthResponse;
            }
            else if (path.EndsWith("/manifest", StringComparison.Ordinal))
            {
                reply = ManifestResponse;
            }
            else
            {
                LastToken = ctx.Request.Headers["X-Darci-Token"];
                using (var reader = new StreamReader(ctx.Request.InputStream))
                    LastInvokeBody = await reader.ReadToEndAsync();

                if (InvokeDelay > TimeSpan.Zero) await Task.Delay(InvokeDelay);
                reply = InvokeResponse;
            }

            var bytes = Encoding.UTF8.GetBytes(reply.Body);
            ctx.Response.StatusCode = reply.Status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch { /* best effort */ }
        }
    }
}
