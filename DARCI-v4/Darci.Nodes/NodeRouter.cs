#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// Default in-process router (decision 1: in-process transport). Resolves the target node by explicit
/// address first, then by requested capability, persists the packet at every hop, and delegates to the
/// node. Persistence happens before and after the node runs so a crash mid-handoff still leaves a
/// recoverable, watchdog-reapable record.
/// </summary>
public sealed class NodeRouter : INodeRouter
{
    private readonly IReadOnlyList<INode> _nodes;
    private readonly INodePacketStore _store;
    private readonly ILogger<NodeRouter> _logger;

    public NodeRouter(IEnumerable<INode> nodes, INodePacketStore store, ILogger<NodeRouter> logger)
    {
        _nodes = nodes.ToList();
        _store = store;
        _logger = logger;
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

        var node = Resolve(packet);
        if (node is null)
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

        _logger.LogInformation("NodeRouter dispatching packet {Id} to {Node}.", packet.Id, node.Id);

        try
        {
            var result = await node.HandleAsync(packet, ct);
            await _store.SavePacketAsync(result, ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "NodeRouter: node {Node} threw handling packet {Id}.", node.Id, packet.Id);
            var aborted = packet.State.IsTerminal()
                ? packet
                : packet.Transition(node.Id, NodeState.Failed,
                    $"Node {node.Id} threw while handling the packet.",
                    success: false, error: $"{ex.GetType().Name}: {ex.Message}");
            await _store.SavePacketAsync(aborted, ct);
            return aborted;
        }
    }

    /// <summary>Explicit address wins; otherwise the first node advertising the requested capability.</summary>
    private INode? Resolve(NodePacket packet)
    {
        if (packet.Address is { } addr)
        {
            var byAddress = _nodes.FirstOrDefault(n => n.Id == addr);
            if (byAddress is not null) return byAddress;
        }

        if (packet.RequestedCapability is { } cap)
            return _nodes.FirstOrDefault(n => n.Capabilities.Contains(cap));

        return null;
    }
}
