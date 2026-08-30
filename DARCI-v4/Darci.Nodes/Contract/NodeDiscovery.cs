#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes;

/// <summary>How discovery behaves. The timeouts are the important part — see <see cref="HandshakeTimeout"/>.</summary>
public sealed record NodeDiscoveryOptions
{
    /// <summary>
    /// Per-node budget for the whole §5.5 handshake (health + manifest).
    ///
    /// <para>BOUNDED ON PURPOSE. Startup network I/O is how a core acquires a hang: the Neo4j fix in this
    /// same branch existed because the driver's own 30-second retry window was inherited rather than
    /// capped. A node that is merely slow must cost this much and no more, and it must cost it ONCE.</para>
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>D4, minimal: the loopback shared secret sent to out-of-process nodes.</summary>
    public string? SharedSecret { get; init; }

    /// <summary>
    /// Refuse endpoints that are not loopback. Phase 3 is local-only by decision; a remote endpoint needs
    /// the real broker-token work, which is deferred. Opt out deliberately, never by accident.
    /// </summary>
    public bool RequireLoopback { get; init; } = true;
}

/// <summary>What discovery did, for logging, tests, and the /nodes diagnostics endpoint.</summary>
public sealed record NodeDiscoveryReport
{
    public IReadOnlyList<string> InProcess { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Remote { get; init; } = Array.Empty<string>();

    /// <summary>node_id (or file path) → why it was not registered. Never silent.</summary>
    public IReadOnlyDictionary<string, string> Skipped { get; init; } = new Dictionary<string, string>();

    public int RegisteredCount => InProcess.Count + Remote.Count;
}

/// <summary>
/// SU 3.4 — doc §5.5 discovery and handshake. Scans <c>nodes/</c>, matches in-process manifests to
/// compiled-in nodes, and for every manifest declaring an <c>endpoint</c> health-checks the running node,
/// verifies its <c>/manifest</c> against the on-disk copy, and registers an <see cref="HttpNodeAdapter"/>.
///
/// <para><b>Nothing here may prevent the core from starting.</b> §5.5 step 5: "the core never dies because
/// a node died." An absent, slow, lying, or duplicate node is skipped with a named reason and the core
/// boots. The only fatal case is a FIRST-PARTY manifest that is invalid — code Tinman compiled in,
/// mis-declared — which is a bug in this repo, not a stranger's problem (Fork 2).</para>
/// </summary>
public sealed class NodeDiscovery
{
    private readonly INodeRegistry _registry;
    private readonly NodeDiscoveryOptions _options;
    private readonly Func<NodeManifest, INodeAdapter> _remoteAdapterFactory;
    private readonly ILogger<NodeDiscovery> _logger;

    public NodeDiscovery(
        INodeRegistry registry,
        NodeDiscoveryOptions options,
        Func<NodeManifest, INodeAdapter> remoteAdapterFactory,
        ILogger<NodeDiscovery>? logger = null)
    {
        _registry = registry;
        _options = options;
        _remoteAdapterFactory = remoteAdapterFactory;
        _logger = logger ?? NullLogger<NodeDiscovery>.Instance;
    }

    /// <summary>
    /// Build the routing table. In-process nodes are registered first, deliberately: Fork 3 says
    /// first-party wins a capability collision, and "first" is only meaningful if it actually goes first.
    /// </summary>
    public async Task<NodeDiscoveryReport> DiscoverAsync(
        IReadOnlyList<LoadedManifest> manifests,
        IReadOnlyList<ManifestLoadFailure> failures,
        IEnumerable<INode> inProcessNodes,
        CancellationToken ct = default)
    {
        var inProcess = new List<string>();
        var remote = new List<string>();
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);

        var byNodeId = manifests.ToDictionary(m => m.Manifest.NodeId, StringComparer.Ordinal);
        var compiledIn = inProcessNodes.ToList();
        var compiledInIds = compiledIn.Select(n => CapabilityKey.From(n.Id)).ToHashSet(StringComparer.Ordinal);

        // ── unreadable manifests (Fork 2) ──
        foreach (var failure in failures)
        {
            // FATAL only when it is ours: a manifest whose node_id names something compiled into THIS core
            // is a repo bug and must be loud. A file too broken to even name itself cannot be shown to be
            // first-party, so it is treated as foreign and skipped — bricking the core over a stranger's
            // malformed JSON is precisely what requirement A forbids.
            if (failure.DeclaredNodeId is { } id && compiledInIds.Contains(id))
            {
                throw new NodeRegistrationException(
                    $"First-party node manifest '{failure.Path}' is invalid and node '{id}' is compiled into this core: {failure.Reason}");
            }

            var key = failure.DeclaredNodeId ?? failure.Path;
            skipped[key] = failure.Reason;
            _logger.LogWarning("Skipping unreadable node manifest {Path}: {Reason}", failure.Path, failure.Reason);
        }

        // ── in-process (first-party wins) ──
        foreach (var node in compiledIn)
        {
            var nodeKey = CapabilityKey.From(node.Id);
            if (!byNodeId.TryGetValue(nodeKey, out var loaded))
            {
                _logger.LogInformation(
                    "Node {NodeId} is compiled in but has no darci-node.json — NOT registered. "
                    + "Its capabilities will be unavailable (requests block honestly). Add a manifest to enable it.",
                    nodeKey);
                skipped[nodeKey] = "compiled in, but no manifest on disk";
                continue;
            }

            if (!loaded.Manifest.IsInProcess)
            {
                // A manifest that names a compiled-in node but points at a URL is ambiguous about which one
                // should serve. Refusing is safer than guessing.
                skipped[nodeKey] = $"manifest declares endpoint '{loaded.Manifest.Endpoint}' but the node is compiled in";
                _logger.LogWarning("Node {NodeId} is compiled in but its manifest declares an endpoint; not registered.", nodeKey);
                continue;
            }

            if (TryRegister(new LegacyPacketNodeAdapter(node, loaded.Manifest), nodeKey, skipped))
                inProcess.Add(nodeKey);
        }

        // ── out-of-process (§5.5 steps 3–5) ──
        foreach (var loaded in manifests.Where(m => !m.Manifest.IsInProcess))
        {
            var manifest = loaded.Manifest;
            var nodeKey = manifest.NodeId;

            if (compiledInIds.Contains(nodeKey)) continue;   // already handled above

            if (_options.RequireLoopback && !IsLoopback(manifest.Endpoint))
            {
                skipped[nodeKey] = $"endpoint '{manifest.Endpoint}' is not loopback (remote nodes need the deferred broker-token work)";
                _logger.LogWarning("Node {NodeId} endpoint {Endpoint} is not loopback; not registered.", nodeKey, manifest.Endpoint);
                continue;
            }

            var adapter = _remoteAdapterFactory(manifest);

            // ONE bounded budget for the whole handshake, so a slow node costs a known amount and the core
            // still starts. This is the Neo4j lesson applied: never inherit someone else's retry window.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.HandshakeTimeout);

            string? failure;
            try
            {
                failure = await HandshakeAsync(adapter, loaded, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = $"handshake exceeded {_options.HandshakeTimeout.TotalSeconds:0.#}s";
            }
            catch (Exception ex)
            {
                failure = ex.GetBaseException().Message;
            }

            if (failure is not null)
            {
                skipped[nodeKey] = failure;
                _logger.LogWarning(
                    "Node {NodeId} at {Endpoint} did not complete the handshake ({Reason}); not registered. "
                    + "Its capabilities will be unavailable until it is running.",
                    nodeKey, manifest.Endpoint, failure);
                continue;
            }

            if (TryRegister(adapter, nodeKey, skipped))
            {
                remote.Add(nodeKey);
                _logger.LogInformation("Registered OUT-OF-PROCESS node {NodeId} at {Endpoint} serving [{Caps}].",
                    nodeKey, manifest.Endpoint, string.Join(", ", manifest.Capabilities.Select(c => c.Name)));
            }
        }

        var report = new NodeDiscoveryReport { InProcess = inProcess, Remote = remote, Skipped = skipped };
        _logger.LogInformation(
            "Node discovery complete: {InProc} in-process, {Remote} out-of-process, {Skipped} skipped.",
            inProcess.Count, remote.Count, skipped.Count);
        return report;
    }

    /// <summary>§5.5 step 3: health, then manifest-match. Returns null on success, else the reason.</summary>
    private static async Task<string?> HandshakeAsync(INodeAdapter adapter, LoadedManifest onDisk, CancellationToken ct)
    {
        if (!await adapter.IsHealthyAsync(ct))
            return "health check failed (node not running or not ready)";

        if (adapter is not HttpNodeAdapter http) return null;

        var reported = await http.FetchManifestAsync(ct);
        if (reported is null)
            return "/manifest was unreachable or unparseable";

        // §5.2: "Must match the on-disk file." The on-disk manifest is the reviewed capability grant; a node
        // reporting something different is claiming a surface nobody approved, which is the one thing static
        // discovery exists to prevent.
        var reportedSha = reported.ComputeSha256();
        if (!string.Equals(reportedSha, onDisk.Sha256, StringComparison.OrdinalIgnoreCase))
            return $"/manifest does not match the on-disk file (disk {Short(onDisk.Sha256)}, node {Short(reportedSha)})";

        return null;
    }

    /// <summary>
    /// Register, converting the registry's strictness into a SKIP. Fork 3: a capability already owned by a
    /// first-party node is not taken by a late claimant, and that collision must not be fatal — the
    /// registry throws by design, and discovery is the layer that decides a stranger's overlap is survivable.
    /// (The registry's `priority` idea stays deliberately unbuilt.)
    /// </summary>
    private bool TryRegister(INodeAdapter adapter, string nodeKey, Dictionary<string, string> skipped)
    {
        try
        {
            _registry.Register(adapter);
            return true;
        }
        catch (NodeRegistrationException ex)
        {
            skipped[nodeKey] = ex.Message;
            _logger.LogWarning("Node {NodeId} was not registered: {Reason}", nodeKey, ex.Message);
            return false;
        }
    }

    internal static bool IsLoopback(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;

        if (uri.IsLoopback) return true;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string sha) => sha.Length <= 12 ? sha : sha[..12];
}
