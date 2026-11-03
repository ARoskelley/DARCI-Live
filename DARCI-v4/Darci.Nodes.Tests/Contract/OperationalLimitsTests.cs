#nullable enable

using System.Net;
using System.Text;
using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// The operational numbers a node author has to know and would otherwise have to guess: what bounds an
/// invocation, whether anything caps a payload, and whether two invocations can be in flight at once.
///
/// <para>These exist because §2 of <c>docs/NODE_REQUIREMENTS.md</c> states them as facts, and several of
/// the honest answers are "nothing enforces this" — which is exactly the kind of claim that rots silently
/// if someone later adds a limit. A test that fails when a cap appears is the cheapest way to keep the
/// document true.</para>
/// </summary>
public sealed class OperationalLimitsTests : IDisposable
{
    private readonly StubNode _node = new();

    public void Dispose() => _node.Dispose();

    private static NodeManifest Manifest(string endpoint, int deadlineMs) => new()
    {
        NodeId = "acme.limits",
        DisplayName = "limits",
        NodeVersion = "0.1.0",
        Endpoint = endpoint,
        Capabilities = new[]
        {
            new NodeCapabilityDescriptor { Name = "acme.work", Description = "work", DeadlineMs = deadlineMs },
        },
    };

    private static NodeInvocation Invocation(int deadlineMs, IReadOnlyDictionary<string, string>? payload = null) => new()
    {
        TraceId = Guid.NewGuid().ToString("N"),
        GoalId = Guid.NewGuid().ToString("N"),
        Capability = "acme.work",
        DeadlineAt = DateTime.UtcNow.AddMilliseconds(deadlineMs),
        Payload = payload ?? new Dictionary<string, string>(),
    };

    // ── what actually bounds an invocation ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheHttpClientsOwnTimeout_CapsTheManifestDeadline_WhicheverIsShorter()
    {
        // The effective budget is min(deadline_ms, HttpClient.Timeout). The core's "darci-node" client is
        // registered with AddHttpClient and no timeout, so in production that cap is the .NET default of
        // 100s — see TheDarciNodeHttpClientDefault_Is100Seconds below. Proven here with a short timeout so
        // the test takes milliseconds rather than two minutes.
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
        var adapter = new HttpNodeAdapter(
            Manifest(_node.BaseUrl, 60_000), http, NullLogger<HttpNodeAdapter>.Instance);
        _node.InvokeDelay = TimeSpan.FromSeconds(3);

        var started = DateTime.UtcNow;
        var result = await adapter.InvokeAsync(Invocation(60_000));
        var elapsed = DateTime.UtcNow - started;

        // A generous manifest deadline did not save it, and the overrun is indistinguishable from a real
        // deadline breach: the same DEADLINE_EXCEEDED, with the same message.
        Assert.Equal(NodeOutcome.Error, result.Outcome);
        Assert.Equal(NodeErrorCode.DeadlineExceeded, result.Error!.Code);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"gave up after {elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public void TheDarciNodeHttpClientDefault_Is100Seconds()
    {
        // Pins the value the document quotes. Program.cs calls AddHttpClient("darci-node") with no
        // configuration, so a node whose capability declares a deadline longer than this cannot ever use
        // the excess — darci.modeling declares 120000.
        using var asRegistered = new HttpClient();

        Assert.Equal(TimeSpan.FromSeconds(100), asRegistered.Timeout);
    }

    [Fact]
    public async Task AnAlreadyExpiredDeadline_SkipsTheNodeEntirely()
    {
        var adapter = new HttpNodeAdapter(
            Manifest(_node.BaseUrl, 1), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        var result = await adapter.InvokeAsync(Invocation(-1));

        Assert.Equal(NodeErrorCode.DeadlineExceeded, result.Error!.Code);
        Assert.Null(_node.LastInvokeBody);      // never contacted
    }

    // ── payload size: nothing in the contract caps it ────────────────────────────────────────────

    [Fact]
    public async Task NothingInTheNodeContract_CapsThePayloadSize()
    {
        // A 4 MB slot round-trips. This is not an endorsement — it documents that the only limits are the
        // core's inbound Kestrel default and whatever your own server imposes, NOT the contract.
        var big = new string('x', 4 * 1024 * 1024);
        _node.InvokeResponse = (200, """{"outcome":"ok","payload":{"echo":"4194304"}}""");
        var adapter = new HttpNodeAdapter(
            Manifest(_node.BaseUrl, 60_000), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        var result = await adapter.InvokeAsync(
            Invocation(60_000, new Dictionary<string, string> { ["blob"] = big }));

        Assert.Equal(NodeOutcome.Ok, result.Outcome);
        // The whole 4 MB slot reached the node: nothing truncated or rejected it on the way out.
        Assert.True(_node.LastInvokeBody!.Length > big.Length,
            $"request body was {_node.LastInvokeBody.Length} bytes, expected more than {big.Length}");
        Assert.Equal("4194304", result.Payload["echo"]);
    }

    // ── concurrency: the dispatch path serializes nothing ────────────────────────────────────────

    [Fact]
    public async Task InvocationsRunInParallel_SoANodeMustBeSafeForConcurrentCalls()
    {
        // The correctness item. Nothing in NodeRouter, NodeDispatcher or HttpNodeAdapter holds a lock or a
        // semaphore, and there is no per-node queue or max-in-flight, so a node can have several /invoke
        // calls open at once. This observes it directly: eight invocations, each held open by the stub
        // until all eight have arrived. If anything serialized them, the gate would never open and this
        // would time out instead of passing.
        const int parallel = 8;
        _node.HoldUntilConcurrent = parallel;
        var adapter = new HttpNodeAdapter(
            Manifest(_node.BaseUrl, 60_000), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        var results = await Task.WhenAll(
            Enumerable.Range(0, parallel).Select(_ => adapter.InvokeAsync(Invocation(60_000))));

        Assert.All(results, r => Assert.Equal(NodeOutcome.Ok, r.Outcome));
        Assert.Equal(parallel, _node.PeakConcurrent);
    }

    // ── taint: how `sources` is actually handled ─────────────────────────────────────────────────

    [Fact]
    public async Task RaisingTheTaintLevel_ReplacesTheSourceList_RatherThanMergingIt()
    {
        // TaintRef.RaisedTo returns whichever TaintRef has the higher level, WHOLE. So a node that raises
        // the level supplies the sources; the request's are not merged in.
        _node.InvokeResponse = (200, """
            {"outcome":"ok","taint":{"level":"untrusted","sources":["https://example.com/a","acme.scraper"]}}
            """);
        var adapter = new HttpNodeAdapter(
            Manifest(_node.BaseUrl, 60_000), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        var result = await adapter.InvokeAsync(Invocation(60_000) with
        {
            Taint = new TaintRef(TaintLevel.Derived, new[] { "request-side-source" }),
        });

        Assert.Equal(TaintLevel.Untrusted, result.Taint.Level);
        Assert.Equal(new[] { "https://example.com/a", "acme.scraper" }, result.Taint.Sources);
        Assert.DoesNotContain("request-side-source", result.Taint.Sources);
    }

    [Fact]
    public async Task NotRaisingTheLevel_DiscardsTheNodesSourceList_Entirely()
    {
        // The other half, and the surprising one: an equal-or-lower level means the REQUEST's TaintRef is
        // kept whole, so sources a node attached to a `clean` result are dropped on the floor.
        _node.InvokeResponse = (200, """
            {"outcome":"ok","taint":{"level":"clean","sources":["this-is-discarded"]}}
            """);
        var adapter = new HttpNodeAdapter(
            Manifest(_node.BaseUrl, 60_000), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        var result = await adapter.InvokeAsync(Invocation(60_000));

        Assert.Equal(TaintLevel.Clean, result.Taint.Level);
        Assert.Empty(result.Taint.Sources);
    }

    [Fact]
    public async Task ASourceEntry_IsNeverParsedOrValidated()
    {
        // Free-form strings. Nothing in the codebase reads an entry, so the format is a CONVENTION the
        // document sets, not a schema the core checks.
        _node.InvokeResponse = (200, """
            {"outcome":"ok","taint":{"level":"derived","sources":["", "not a url", "  padded  "]}}
            """);
        var adapter = new HttpNodeAdapter(
            Manifest(_node.BaseUrl, 60_000), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        var result = await adapter.InvokeAsync(Invocation(60_000));

        Assert.Equal(TaintLevel.Derived, result.Taint.Level);
        Assert.Equal(new[] { "", "not a url", "  padded  " }, result.Taint.Sources);
    }

    [Fact]
    public async Task ADependencyKindMustBeHyphenated_OrItSilentlyBecomesPendingOutcome()
    {
        // §9.5. `missing_environment` is not recognized and degrades without a warning, which turns
        // "nothing here can run this" into "waiting for a real-world result" - a different decision.
        var adapter = new HttpNodeAdapter(
            Manifest(_node.BaseUrl, 60_000), new HttpClient(), NullLogger<HttpNodeAdapter>.Instance);

        _node.InvokeResponse = (200, """{"outcome":"blocked","dependency":{"kind":"missing-environment","detail":"d"}}""");
        var hyphenated = await adapter.InvokeAsync(Invocation(60_000));

        _node.InvokeResponse = (200, """{"outcome":"blocked","dependency":{"kind":"missing_environment","detail":"d"}}""");
        var underscored = await adapter.InvokeAsync(Invocation(60_000));

        Assert.Equal(DependencyKind.MissingEnvironment, hyphenated.Dependency!.Kind);
        Assert.Equal(DependencyKind.PendingOutcome, underscored.Dependency!.Kind);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private sealed class StubNode : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;

        public string BaseUrl { get; }
        public (int Status, string Body) InvokeResponse = (200, """{"outcome":"ok"}""");
        public TimeSpan InvokeDelay = TimeSpan.Zero;
        public int HoldUntilConcurrent;
        public int PeakConcurrent;
        public string? LastInvokeBody;

        public StubNode()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

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

                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(ctx); }
                    catch { /* the stub must never take the test host down */ }
                });
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var (status, body) = (200, """{"status":"ok"}""");

            if (path.EndsWith("/invoke", StringComparison.Ordinal))
            {
                using (var reader = new StreamReader(ctx.Request.InputStream))
                    LastInvokeBody = await reader.ReadToEndAsync();

                var now = Interlocked.Increment(ref _inFlight);
                InterlockedMax(ref PeakConcurrent, now);

                if (HoldUntilConcurrent > 0)
                {
                    // Open the gate only once every expected caller is simultaneously inside.
                    if (now >= HoldUntilConcurrent) _gate.TrySetResult();
                    await _gate.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }

                if (InvokeDelay > TimeSpan.Zero) await Task.Delay(InvokeDelay);
                Interlocked.Decrement(ref _inFlight);
                (status, body) = InvokeResponse;
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            do
            {
                seen = Volatile.Read(ref target);
                if (value <= seen) return;
            }
            while (Interlocked.CompareExchange(ref target, value, seen) != seen);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _gate.TrySetResult();
            try { _listener.Stop(); } catch { /* best effort */ }
            _cts.Dispose();
        }
    }
}
