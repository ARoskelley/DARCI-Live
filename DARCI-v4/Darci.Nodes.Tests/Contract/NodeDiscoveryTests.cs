#nullable enable

using System.Net;
using System.Text;
using System.Text.Json;
using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// SU 3.4 — doc §5.5 discovery and handshake.
///
/// <para>The rule every test here defends is step 5: <b>"the core never dies because a node died."</b> An
/// absent, slow, lying, or duplicate node must be skipped with a named reason while the core carries on.
/// The single exception is a FIRST-PARTY manifest that is invalid (Fork 2) — code compiled into this core,
/// mis-declared — which is a repo bug and must be loud.</para>
/// </summary>
public sealed class NodeDiscoveryTests : IDisposable
{
    private readonly StubNode _node = new();
    private readonly string _dir;

    public NodeDiscoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"darci-disco-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _node.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── fixtures ──

    private static NodeManifest RemoteManifest(string endpoint, string nodeId = "acme.summarize", string capability = "summarize.text") => new()
    {
        NodeId = nodeId,
        DisplayName = "Acme",
        NodeVersion = "1.0.0",
        Endpoint = endpoint,
        Health = "/health",
        Capabilities = new[] { new NodeCapabilityDescriptor { Name = capability, Description = "d" } },
    };

    private string WriteManifest(NodeManifest manifest, string folder)
    {
        var dir = Path.Combine(_dir, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, NodeManifestLoader.ManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJson.Options));
        return path;
    }

    private (NodeRegistry Registry, NodeDiscovery Discovery) Build(NodeDiscoveryOptions? options = null)
    {
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        var discovery = new NodeDiscovery(
            registry,
            options ?? new NodeDiscoveryOptions { HandshakeTimeout = TimeSpan.FromSeconds(3) },
            m => new HttpNodeAdapter(m, new HttpClient(), NullLogger<HttpNodeAdapter>.Instance),
            NullLogger<NodeDiscovery>.Instance);
        return (registry, discovery);
    }

    private (IReadOnlyList<LoadedManifest> Loaded, IReadOnlyList<ManifestLoadFailure> Failures) Scan() =>
        new NodeManifestLoader(NullLogger<NodeManifestLoader>.Instance).LoadAllTolerant(_dir);

    private sealed class FakeNode : INode
    {
        public FakeNode(NodeId id, params Capability[] caps)
        {
            Id = id;
            Capabilities = new HashSet<Capability>(caps);
        }
        public NodeId Id { get; }
        public IReadOnlySet<Capability> Capabilities { get; }
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default) =>
            Task.FromResult(packet.Transition(Id, NodeState.Succeeded, "done", success: true));
    }

    // ── the out-of-process happy path ──

    [Fact]
    public async Task AHealthyNodeWithAMatchingManifest_IsRegisteredOverHttp()
    {
        var manifest = RemoteManifest(_node.BaseUrl);
        WriteManifest(manifest, "acme");
        _node.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJson.Options);

        var (registry, discovery) = Build();
        var (loaded, failures) = Scan();
        var report = await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        Assert.Contains("acme.summarize", report.Remote);
        Assert.NotNull(registry.Resolve("summarize.text"));
        Assert.Empty(report.Skipped);
    }

    [Fact]
    public async Task ARegisteredRemoteNode_ActuallyDispatches()
    {
        // Registration is only worth anything if the capability then routes end-to-end.
        var manifest = RemoteManifest(_node.BaseUrl);
        WriteManifest(manifest, "acme");
        _node.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJson.Options);
        _node.InvokeJson = """{"outcome":"ok","payload":{"knowledge_findings":"remote answer"}}""";

        var (registry, discovery) = Build();
        var (loaded, failures) = Scan();
        await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        var registration = registry.Resolve("summarize.text")!;
        var result = await registration.Adapter.InvokeAsync(new NodeInvocation
        {
            GoalId = "g1",
            Capability = "summarize.text",
            DeadlineAt = DateTime.UtcNow.AddSeconds(20),
        });

        Assert.Equal(NodeOutcome.Ok, result.Outcome);
        Assert.Equal("remote answer", result.Payload["knowledge_findings"]);
    }

    // ── §5.5 step 5: skip, never die ──

    [Fact]
    public async Task AnUnreachableDeclaredNode_IsSkippedAndTheCoreStillBoots()
    {
        WriteManifest(RemoteManifest("http://127.0.0.1:1"), "ghost");

        var (registry, discovery) = Build();
        var (loaded, failures) = Scan();
        var report = await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        Assert.Empty(report.Remote);
        Assert.Contains("acme.summarize", report.Skipped.Keys);
        Assert.Null(registry.Resolve("summarize.text"));
    }

    [Fact]
    public async Task ANodeThatIsUpButNotReady_IsSkipped()
    {
        var manifest = RemoteManifest(_node.BaseUrl);
        WriteManifest(manifest, "acme");
        _node.HealthJson = """{"status":"starting"}""";

        var (_, discovery) = Build();
        var (loaded, failures) = Scan();
        var report = await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        Assert.Contains("acme.summarize", report.Skipped.Keys);
        Assert.Contains("health", report.Skipped["acme.summarize"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ANodeWhoseManifestDoesNotMatchTheOnDiskCopy_IsSkipped()
    {
        // §5.2: /manifest "must match the on-disk file". The on-disk copy is the reviewed capability grant;
        // a node reporting a different one is claiming a surface nobody approved, which is exactly what
        // static discovery exists to prevent.
        var onDisk = RemoteManifest(_node.BaseUrl);
        WriteManifest(onDisk, "acme");
        _node.ManifestJson = JsonSerializer.Serialize(
            onDisk with { Capabilities = new[] { new NodeCapabilityDescriptor { Name = "summarize.text", Description = "d" },
                                                 new NodeCapabilityDescriptor { Name = "delete.everything", Description = "extra" } } },
            ManifestJson.Options);

        var (registry, discovery) = Build();
        var (loaded, failures) = Scan();
        var report = await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        Assert.Contains("acme.summarize", report.Skipped.Keys);
        Assert.Contains("does not match", report.Skipped["acme.summarize"]);
        Assert.Null(registry.Resolve("delete.everything"));   // the unapproved verb never enters routing
    }

    [Fact]
    public async Task ASlowNode_IsBoundedByTheHandshakeTimeout_NotLeftToHang()
    {
        // The Neo4j lesson: startup network I/O must never inherit somebody else's retry window.
        var manifest = RemoteManifest(_node.BaseUrl);
        WriteManifest(manifest, "slow");
        _node.HealthDelay = TimeSpan.FromSeconds(10);

        var (_, discovery) = Build(new NodeDiscoveryOptions { HandshakeTimeout = TimeSpan.FromSeconds(1) });
        var (loaded, failures) = Scan();

        var started = DateTime.UtcNow;
        var report = await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());
        var elapsed = DateTime.UtcNow - started;

        Assert.Contains("acme.summarize", report.Skipped.Keys);
        Assert.True(elapsed < TimeSpan.FromSeconds(8),
            $"discovery took {elapsed.TotalSeconds:0.#}s — a slow node must not stall startup.");
    }

    [Fact]
    public async Task ANonLoopbackEndpoint_IsRefusedWhileRemoteAuthIsDeferred()
    {
        WriteManifest(RemoteManifest("http://198.51.100.7:9000"), "offbox");

        var (_, discovery) = Build();
        var (loaded, failures) = Scan();
        var report = await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        Assert.Contains("acme.summarize", report.Skipped.Keys);
        Assert.Contains("loopback", report.Skipped["acme.summarize"]);
    }

    // ── Fork 2: whose broken manifest is fatal ──

    [Fact]
    public async Task AStrangersMalformedManifest_IsSkipped_NotFatal()
    {
        // Requirement A: a third party's broken file must never brick Tinman's core.
        var dir = Path.Combine(_dir, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, NodeManifestLoader.ManifestFileName), "{ this is not json");

        var (_, discovery) = Build();
        var (loaded, failures) = Scan();

        var report = await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        Assert.NotEmpty(report.Skipped);
        Assert.Equal(0, report.RegisteredCount);
    }

    [Fact]
    public async Task AFirstPartyManifestThatIsInvalid_IsFatalAndNamed()
    {
        // The other half of Fork 2: a manifest naming a node compiled into THIS core is a repo bug.
        var dir = Path.Combine(_dir, "coding");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, NodeManifestLoader.ManifestFileName),
            """{"node_id":"darci.coding","capabilities":[]}""");   // no capabilities ⇒ invalid

        var (_, discovery) = Build();
        var (loaded, failures) = Scan();

        var ex = await Assert.ThrowsAsync<NodeRegistrationException>(() =>
            discovery.DiscoverAsync(loaded, failures, new INode[] { new FakeNode(NodeId.Coding, Capability.WriteCode) }));

        Assert.Contains("darci.coding", ex.Message);
        Assert.Contains("First-party", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fork 3: capability collisions ──

    [Fact]
    public async Task AnExternalNodeClaimingAFirstPartyCapability_IsSkipped_FirstPartyWins()
    {
        // In-process registers first, deliberately, so "first-party wins" is a fact about ordering rather
        // than a hope. The late claimant is skipped with a named reason instead of killing startup.
        var coding = new FakeNode(NodeId.Coding, Capability.WriteCode);
        WriteManifest(new NodeManifest
        {
            NodeId = "darci.coding",
            DisplayName = "Coding",
            NodeVersion = "1.0.0",
            Capabilities = new[] { new NodeCapabilityDescriptor { Name = Capabilities.CodingWrite, Description = "d" } },
        }, "coding");

        var squatter = RemoteManifest(_node.BaseUrl, nodeId: "acme.coding", capability: Capabilities.CodingWrite);
        WriteManifest(squatter, "squatter");
        _node.ManifestJson = JsonSerializer.Serialize(squatter, ManifestJson.Options);

        var (registry, discovery) = Build();
        var (loaded, failures) = Scan();
        var report = await discovery.DiscoverAsync(loaded, failures, new INode[] { coding });

        Assert.Contains("darci.coding", report.InProcess);
        Assert.Contains("acme.coding", report.Skipped.Keys);
        Assert.Equal("darci.coding", registry.Resolve(Capabilities.CodingWrite)!.NodeId);
    }

    // ── in-process gating still holds (SU 3.2) ──

    [Fact]
    public async Task ACompiledInNodeWithNoManifest_IsNotRegistered()
    {
        var (registry, discovery) = Build();
        var (loaded, failures) = Scan();

        var report = await discovery.DiscoverAsync(
            loaded, failures, new INode[] { new FakeNode(NodeId.Coding, Capability.WriteCode) });

        Assert.Empty(report.InProcess);
        Assert.Null(registry.Resolve(Capabilities.CodingWrite));
        Assert.Contains("darci.coding", report.Skipped.Keys);
    }

    [Fact]
    public async Task ACoreWithNothingDeclared_DiscoversNothingAndDoesNotThrow()
    {
        var (registry, discovery) = Build();
        var (loaded, failures) = Scan();

        var report = await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        Assert.Equal(0, report.RegisteredCount);
        Assert.Empty(registry.Registrations);
    }

    // ── Degraded (§5.5 step 5) rides the existing seam ──

    [Fact]
    public async Task ADegradedNodesCapabilitiesLeaveRouting_ButTheNodeStaysKnown()
    {
        var manifest = RemoteManifest(_node.BaseUrl);
        WriteManifest(manifest, "acme");
        _node.ManifestJson = JsonSerializer.Serialize(manifest, ManifestJson.Options);

        var (registry, discovery) = Build();
        var (loaded, failures) = Scan();
        await discovery.DiscoverAsync(loaded, failures, Array.Empty<INode>());

        Assert.True(registry.SetDegraded("acme.summarize", true));

        // Degraded means unroutable by EVERY path — capability and explicit address alike. Leaving the
        // address path open would let a packet reach a node the core has already decided is not serving.
        Assert.Null(registry.Resolve("summarize.text"));
        Assert.Null(registry.ResolveNode("acme.summarize"));

        // But it stays KNOWN, so /nodes can report it rather than the node just vanishing.
        Assert.Contains(registry.Registrations, r => r.NodeId == "acme.summarize");

        // And it comes back without re-registration once healthy again.
        Assert.True(registry.SetDegraded("acme.summarize", false));
        Assert.NotNull(registry.Resolve("summarize.text"));
    }

    [Fact]
    public void IsLoopback_AcceptsLocalAndRejectsRemote()
    {
        Assert.True(NodeDiscovery.IsLoopback("http://localhost:7801"));
        Assert.True(NodeDiscovery.IsLoopback("http://127.0.0.1:7801"));
        Assert.False(NodeDiscovery.IsLoopback("http://198.51.100.7:7801"));
        Assert.False(NodeDiscovery.IsLoopback("not a uri"));
        Assert.False(NodeDiscovery.IsLoopback(null));
    }

    /// <summary>A real node process stand-in over a real socket.</summary>
    private sealed class StubNode : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public string BaseUrl { get; }
        public string HealthJson = """{"status":"ok"}""";
        public string ManifestJson = "{}";
        public string InvokeJson = """{"outcome":"ok"}""";
        public TimeSpan HealthDelay = TimeSpan.Zero;

        public StubNode()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();

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

                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "";
                    string body;
                    if (path.EndsWith("/health", StringComparison.Ordinal))
                    {
                        if (HealthDelay > TimeSpan.Zero) await Task.Delay(HealthDelay);
                        body = HealthJson;
                    }
                    else if (path.EndsWith("/manifest", StringComparison.Ordinal)) body = ManifestJson;
                    else body = InvokeJson;

                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { /* the stub must never take the test host down */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch { /* best effort */ }
        }
    }
}
