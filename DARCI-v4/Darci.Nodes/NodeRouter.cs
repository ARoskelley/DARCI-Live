#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes;

/// <summary>
/// The routing seam. Resolves a packet's target node, persists the packet at every hop, and hands it to the
/// node through the ONE dispatch point (<see cref="NodeDispatcher"/>). Persistence happens before and after
/// the node runs so a crash mid-handoff still leaves a recoverable, watchdog-reapable record.
///
/// <para><b>Phase 1 (SU3):</b> resolution moved from a compiled-in <see cref="Capability"/> enum scan to the
/// string-keyed <see cref="INodeRegistry"/>, and invocation moved to <see cref="NodeDispatcher"/>. The public
/// surface (<see cref="INodeRouter.DispatchAsync"/>) is deliberately UNCHANGED, so every existing call site
/// and test keeps working — that is the behavior-preservation contract of this sub-unit.</para>
/// </summary>
public sealed class NodeRouter : INodeRouter
{
    private readonly INodeRegistry _registry;
    private readonly NodeDispatcher _dispatcher;
    private readonly INodePacketStore _store;
    private readonly ILogger<NodeRouter> _logger;

    /// <summary>Primary constructor: routing driven by the manifest-backed registry.</summary>
    public NodeRouter(
        INodeRegistry registry,
        NodeDispatcher dispatcher,
        INodePacketStore store,
        ILogger<NodeRouter> logger)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// COMPATIBILITY constructor (transitional, retired in SU6): takes packet-native <see cref="INode"/>s,
    /// wraps each in a <see cref="LegacyPacketNodeAdapter"/> with a synthesized manifest, and registers them.
    ///
    /// <para>Capability overlap is TOLERATED here (first-wins by registration order) because that is what the
    /// pre-carve router did — an existing configuration registers two nodes both declaring
    /// <see cref="Capability.WriteCode"/>. Manifest-driven registration is strict instead. See the SU3 fork note.</para>
    /// </summary>
    public NodeRouter(IEnumerable<INode> nodes, INodePacketStore store, ILogger<NodeRouter> logger)
        : this(BuildLegacyRegistry(nodes), new NodeDispatcher(NullLogger<NodeDispatcher>.Instance), store, logger)
    {
    }

    public async Task<NodePacket> DispatchAsync(NodePacket packet, CancellationToken ct = default)
    {
        // Persist the packet up front so it exists even if resolution or the node fails.
        if (packet.State == NodeState.Created)
        {
            packet = packet.Transition(NodeId.Orchestrator, NodeState.Routed,
                $"Routed (address={packet.Address?.ToString() ?? "—"}, capability={packet.RequestedCapability?.ToString() ?? "—"})");
            await _store.CreatePacketAsync(packet, ct);
        }
        else
        {
            await _store.SavePacketAsync(packet, ct);
        }

        var (registration, capability) = Resolve(packet);
        if (registration is null)
        {
            var failed = packet.State.IsTerminal()
                ? packet
                : packet.Transition(NodeId.Orchestrator, NodeState.Failed,
                    "No node could be resolved for this packet.",
                    success: false,
                    error: $"No node matches address={packet.Address?.ToString() ?? "—"} / capability={packet.RequestedCapability?.ToString() ?? "—"}.");
            await _store.SavePacketAsync(failed, ct);
            _logger.LogWarning("NodeRouter: no node for packet {Id} (address={Addr}, capability={Cap}).",
                packet.Id, packet.Address, packet.RequestedCapability);
            return failed;
        }

        _logger.LogInformation("NodeRouter dispatching packet {Id} to {Node}.", packet.Id, registration.NodeId);

        try
        {
            var result = await _dispatcher.DispatchAsync(registration, packet, capability, ct);
            await _store.SavePacketAsync(result, ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var legacyNodeId = CapabilityKey.ToLegacyNode(registration.NodeId) ?? NodeId.Orchestrator;
            _logger.LogError(ex, "NodeRouter: node {Node} threw handling packet {Id}.", legacyNodeId, packet.Id);
            var aborted = packet.State.IsTerminal()
                ? packet
                : packet.Transition(legacyNodeId, NodeState.Failed,
                    $"Node {legacyNodeId} threw while handling the packet.",
                    success: false, error: $"{ex.GetType().Name}: {ex.Message}");
            await _store.SavePacketAsync(aborted, ct);
            return aborted;
        }
    }

    /// <summary>Explicit address wins; otherwise the node registered for the requested capability.</summary>
    private (NodeRegistration? Registration, string Capability) Resolve(NodePacket packet)
    {
        var requestedCapability = packet.RequestedCapability is { } cap ? CapabilityKey.From(cap) : "";

        if (packet.Address is { } addr)
        {
            var byAddress = _registry.ResolveNode(CapabilityKey.From(addr));
            if (byAddress is not null)
            {
                // Prefer the requested capability when this node actually serves it, so telemetry and the
                // per-invocation deadline reflect the real verb rather than a guess.
                var capability = byAddress.Manifest.Capabilities.Any(c => c.Name == requestedCapability)
                    ? requestedCapability
                    : byAddress.Manifest.Capabilities.Count == 1
                        ? byAddress.Manifest.Capabilities[0].Name
                        : requestedCapability;
                return (byAddress, capability);
            }
        }

        if (requestedCapability.Length > 0)
        {
            var byCapability = _registry.Resolve(requestedCapability);
            if (byCapability is not null) return (byCapability, requestedCapability);
        }

        return (null, requestedCapability);
    }

    private static INodeRegistry BuildLegacyRegistry(IEnumerable<INode> nodes)
    {
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        foreach (var node in nodes)
            registry.Register(LegacyPacketNodeAdapter.ForLegacyNode(node), tolerateCapabilityOverlap: true);
        return registry;
    }
}
