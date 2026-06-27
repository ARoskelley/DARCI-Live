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
    private readonly IWorkContextResolver? _workspaceResolver;
    private readonly ILogger<CodingNode> _logger;

    public CodingNode(
        ICodingTaskService taskService,
        Lazy<ICodingAgentLoop> loop,
        ILogger<CodingNode> logger,
        IWorkContextResolver? workspaceResolver = null)
    {
        _taskService = taskService;
        _loop = loop;
        _logger = logger;
        _workspaceResolver = workspaceResolver;
    }

    public NodeId Id => NodeId.Coding;

    public IReadOnlySet<Capability> Capabilities { get; } =
        new HashSet<Capability> { Capability.WriteCode, Capability.RunTests };

    public async Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
    {
        // Routed → Accepted → Working (the loop adopts this packet and continues its log from here).
        if (packet.State == NodeState.Routed)
            packet = packet.Transition(NodeId.Coding, NodeState.Accepted, "Coding node accepted task");
        if (packet.State == NodeState.Accepted)
            packet = packet.Transition(NodeId.Coding, NodeState.Working, "Creating coding task", leaseFor: CodingNodeTracker.LeaseDuration);

        // Resolve the workspace: use the slot if present, else ask the resolver to pick or create one
        // and record that decision (with confidence) on the packet log.
        var workspaceId = packet.Payload.Slot(PacketSlots.WorkspaceId);
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            if (_workspaceResolver is null)
                return packet.Transition(NodeId.Coding, NodeState.Failed,
                    "No workspace specified and no workspace resolver is configured.",
                    success: false, error: $"Set the '{PacketSlots.WorkspaceId}' slot or wire an IWorkContextResolver.");

            var resolution = await _workspaceResolver.ResolveAsync(packet.Payload.Intent, ct);
            if (string.IsNullOrWhiteSpace(resolution.ContextId))
                return packet.Transition(NodeId.Coding, NodeState.Failed,
                    "Workspace resolution returned no workspace.", success: false, error: resolution.Reasoning);

            workspaceId = resolution.ContextId;
            packet = packet
                .WithSlot(PacketSlots.WorkspaceId, workspaceId)
                .Transition(NodeId.Coding, NodeState.Working,
                    $"Workspace {(resolution.Created ? "created" : "reused")}: {resolution.Reasoning}",
                    confidence: resolution.Confidence,
                    artifacts: new[] { workspaceId });
        }

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
