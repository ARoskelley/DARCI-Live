#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// The coding node as an <see cref="INode"/> (Step B). Lets DARCI's cognition route a packet to the
/// coding subsystem through the generic router instead of a coding-specific call. Creates a coding
/// task from the packet's intent and starts the agent loop bound to that same packet (so the run's
/// audit log lives on the routed packet, not a fresh one), then returns the packet in
/// <see cref="NodeState.Working"/> for the caller to poll (decision 1).
///
/// Required input slot: <see cref="PacketSlots.WorkspaceId"/>.
/// </summary>
public sealed class CodingNode : INode
{
    private readonly ICodingTaskService _taskService;
    // Lazy to break the DI cycle: the router depends on this node, the loop depends on the router.
    private readonly Lazy<ICodingAgentLoop> _loop;
    private readonly ILogger<CodingNode> _logger;

    public CodingNode(ICodingTaskService taskService, Lazy<ICodingAgentLoop> loop, ILogger<CodingNode> logger)
    {
        _taskService = taskService;
        _loop = loop;
        _logger = logger;
    }

    public NodeId Id => NodeId.Coding;

    public IReadOnlySet<Capability> Capabilities { get; } =
        new HashSet<Capability> { Capability.WriteCode, Capability.RunTests };

    public async Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
    {
        var workspaceId = packet.Payload.Slot(PacketSlots.WorkspaceId);
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return packet.State.IsTerminal()
                ? packet
                : packet.Transition(NodeId.Coding, NodeState.Failed,
                    "Coding packet missing required workspaceId slot.",
                    success: false, error: $"Set the '{PacketSlots.WorkspaceId}' slot before routing to the coding node.");
        }

        // Routed → Accepted → Working (the loop adopts this packet and continues its log from here).
        if (packet.State == NodeState.Routed)
            packet = packet.Transition(NodeId.Coding, NodeState.Accepted, "Coding node accepted task");
        if (packet.State == NodeState.Accepted)
            packet = packet.Transition(NodeId.Coding, NodeState.Working, "Creating coding task", leaseFor: CodingNodeTracker.LeaseDuration);

        var task = await _taskService.CreateTaskAsync(
            new CreateCodingTaskRequest(workspaceId, packet.Payload.Intent, packet.Payload.SuccessCriteria), ct);

        packet = packet.WithSlot(PacketSlots.CodingTaskId, task.Id);

        // Start the loop bound to this packet (non-blocking). The loop's tracker adopts the packet and
        // drives it to a terminal state; callers poll via the packet store.
        var started = _loop.Value.StartLoop(task.Id, options: null, rootPacket: packet);
        _logger.LogInformation("CodingNode started loop for task {TaskId} (packet {PacketId}, started={Started}).",
            task.Id, packet.Id, started);

        return packet;
    }
}
