#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>One registered node: its manifest, its invocation surface, and the audit anchor.</summary>
public sealed record NodeRegistration(
    NodeManifest Manifest,
    INodeAdapter Adapter,
    string ManifestSha256,
    DateTime RegisteredAt)
{
    public string NodeId => Manifest.NodeId;
    public bool Degraded { get; init; }
}

/// <summary>Thrown when registration fails. Doc §5.5: "Failure here is fatal and named" — never silent.</summary>
public sealed class NodeRegistrationException : Exception
{
    public NodeRegistrationException(string message) : base(message) { }
}

/// <summary>
/// The router table (doc §5.5 step 4): resolves a STRING capability verb to the node that serves it.
///
/// <para>This is the C1 fix. Because the key is a string that comes from a manifest — not a compiled-in
/// <see cref="Capability"/> enum member — a collaborator's node can declare a capability this core has never
/// heard of and be routed to, with no core recompile.</para>
/// </summary>
public interface INodeRegistry
{
    /// <summary>Register a node. Throws <see cref="NodeRegistrationException"/> on any validation failure.</summary>
    NodeRegistration Register(INodeAdapter adapter);

    /// <summary>The node serving <paramref name="capability"/>, or null if nothing does.</summary>
    NodeRegistration? Resolve(string capability);

    /// <summary>Resolve by node id (the explicit-address path).</summary>
    NodeRegistration? ResolveNode(string nodeId);

    IReadOnlyList<NodeRegistration> Registrations { get; }

    /// <summary>Every routable capability, sorted. Useful for diagnostics and the future /nodes endpoint.</summary>
    IReadOnlyList<string> RoutableCapabilities { get; }

    /// <summary>Mark a node degraded (doc §5.5 step 5): its capabilities leave the routing table, the core
    /// keeps running. "The core never dies because a node died."</summary>
    bool SetDegraded(string nodeId, bool degraded);
}

public sealed class NodeRegistry : INodeRegistry
{
    private readonly object _gate = new();
    private readonly List<NodeRegistration> _registrations = new();
    private readonly Dictionary<string, NodeRegistration> _byCapability = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeRegistration> _byNodeId = new(StringComparer.Ordinal);
    private readonly ILogger<NodeRegistry> _logger;

    public NodeRegistry(ILogger<NodeRegistry> logger) => _logger = logger;

    public NodeRegistration Register(INodeAdapter adapter)
    {
        var manifest = adapter.Manifest;

        // Doc §5.5 step 2 — validate, and fail FATALLY and NAMED.
        var errors = manifest.Validate();
        if (errors.Count > 0)
            throw new NodeRegistrationException(
                $"Node '{manifest.NodeId}' failed manifest validation: {string.Join(" | ", errors)}");

        lock (_gate)
        {
            if (_byNodeId.ContainsKey(manifest.NodeId))
                throw new NodeRegistrationException($"Node id '{manifest.NodeId}' is already registered.");

            // A capability may be served by exactly one node — otherwise routing is ambiguous.
            foreach (var c in manifest.Capabilities)
                if (_byCapability.TryGetValue(c.Name, out var owner))
                    throw new NodeRegistrationException(
                        $"Capability '{c.Name}' is already served by node '{owner.NodeId}'; " +
                        $"node '{manifest.NodeId}' cannot also claim it.");

            var registration = new NodeRegistration(manifest, adapter, manifest.ComputeSha256(), DateTime.UtcNow);
            _registrations.Add(registration);
            _byNodeId[manifest.NodeId] = registration;
            foreach (var c in manifest.Capabilities) _byCapability[c.Name] = registration;

            _logger.LogInformation(
                "Registered node {NodeId} v{Version} ({Kind}, {InProc}) serving [{Caps}] — manifest sha256 {Sha}.",
                manifest.NodeId, manifest.NodeVersion, manifest.Kind,
                manifest.IsInProcess ? "in-process" : manifest.Endpoint,
                string.Join(", ", manifest.Capabilities.Select(c => c.Name)),
                registration.ManifestSha256[..16]);

            return registration;
        }
    }

    public NodeRegistration? Resolve(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability)) return null;
        lock (_gate)
            return _byCapability.TryGetValue(capability, out var r) && !r.Degraded ? r : null;
    }

    public NodeRegistration? ResolveNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return null;
        lock (_gate)
            return _byNodeId.TryGetValue(nodeId, out var r) && !r.Degraded ? r : null;
    }

    public IReadOnlyList<NodeRegistration> Registrations
    {
        get { lock (_gate) return _registrations.ToList(); }
    }

    public IReadOnlyList<string> RoutableCapabilities
    {
        get
        {
            lock (_gate)
                return _byCapability.Where(kv => !kv.Value.Degraded)
                    .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
    }

    public bool SetDegraded(string nodeId, bool degraded)
    {
        lock (_gate)
        {
            if (!_byNodeId.TryGetValue(nodeId, out var current)) return false;
            var updated = current with { Degraded = degraded };
            _byNodeId[nodeId] = updated;
            var idx = _registrations.FindIndex(r => r.NodeId == nodeId);
            if (idx >= 0) _registrations[idx] = updated;
            foreach (var c in updated.Manifest.Capabilities) _byCapability[c.Name] = updated;

            _logger.LogWarning("Node {NodeId} marked {State}; its capabilities are {Action} the routing table.",
                nodeId, degraded ? "DEGRADED" : "healthy", degraded ? "removed from" : "restored to");
            return true;
        }
    }
}
