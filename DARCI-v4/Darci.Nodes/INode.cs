#nullable enable

namespace Darci.Nodes;

/// <summary>
/// A node is a specialized worker (coding, knowledge, engineering, …) that acts on packets routed to
/// it. Nodes are addressed either by explicit <see cref="NodeId"/> or by an advertised
/// <see cref="Capability"/> — the router resolves which. This is the generic seam (decision 2): new
/// nodes slot in by implementing this interface and registering; nothing is coding-specific.
/// </summary>
public interface INode
{
    NodeId Id { get; }

    /// <summary>Capabilities this node can satisfy when a packet requests one instead of an address.</summary>
    IReadOnlySet<Capability> Capabilities { get; }

    /// <summary>
    /// Act on a packet addressed to this node. Returns the packet with its state advanced and a log
    /// entry appended. May run to completion (e.g. knowledge research) or start async work and return
    /// the packet in <see cref="NodeState.Working"/> (e.g. a coding run) for the caller to poll.
    /// </summary>
    Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default);
}

/// <summary>
/// Resolves a packet's target node (by address or capability), persists the packet, and hands it to
/// the node. The single choke point through which all cross-node work flows, so every hop is recorded
/// and retrievable (decision 3 + 5).
/// </summary>
public interface INodeRouter
{
    /// <summary>
    /// Route <paramref name="packet"/> to its target node and return the resulting packet. If no node
    /// can be resolved, the packet is driven to <see cref="NodeState.Failed"/> with an explanatory log
    /// entry rather than throwing.
    /// </summary>
    Task<NodePacket> DispatchAsync(NodePacket packet, CancellationToken ct = default);
}
