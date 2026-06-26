#nullable enable

using System.Collections.Concurrent;
using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Bridges the coding agent loop to the node-packet protocol (Phase 0). Every coding task run is
/// mirrored as a <see cref="NodePacket"/> addressed to <see cref="NodeId.Coding"/>: the loop's
/// progress becomes an append-only audit log, and the packet holds a renewing lease so a hung or
/// crashed run is reaped by the watchdog instead of orphaning at "in_progress".
///
/// Every operation is best-effort and swallows its own errors — the packet layer must never break
/// the coding loop it observes. The current packet is held in memory per task (the loop is
/// single-threaded per task) and persisted on each update.
/// </summary>
public sealed class CodingNodeTracker
{
    // Generous: a single LLM coding step can take many minutes on local hardware. The lease is
    // renewed on every recorded step, so this only bites when a step truly hangs past the window.
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    private readonly INodePacketStore? _store;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, NodePacket> _byTask = new();

    public CodingNodeTracker(INodePacketStore? store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    public bool Enabled => _store is not null;

    /// <summary>Mint a packet for a task and drive it Created → Routed → Accepted → Working.</summary>
    public async Task BeginAsync(string taskId, string workspaceId, string intent, string? successCriteria, CancellationToken ct)
    {
        if (_store is null) return;
        try
        {
            var packet = NodePacket.Create(
                    intent: intent,
                    successCriteria: successCriteria,
                    address: NodeId.Coding,
                    capability: Capability.WriteCode,
                    correlationId: taskId,
                    slots: new Dictionary<string, string>
                    {
                        ["codingTaskId"] = taskId,
                        ["workspaceId"] = workspaceId,
                    })
                .Transition(NodeId.Coding, NodeState.Routed, "Routed to coding node")
                .Transition(NodeId.Coding, NodeState.Accepted, "Coding loop accepted task")
                .Transition(NodeId.Coding, NodeState.Working, "Coding loop started", leaseFor: LeaseDuration);

            await _store.CreatePacketAsync(packet, ct);
            _byTask[taskId] = packet;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Node packet begin failed for task {TaskId} (non-fatal).", taskId);
        }
    }

    /// <summary>Append a Working-state log entry and renew the lease (heartbeat + audit).</summary>
    public async Task RecordAsync(
        string taskId,
        string decision,
        double confidenceScore = -1.0,
        string? confidenceNote = null,
        bool? success = null,
        string? error = null,
        IReadOnlyList<string>? artifacts = null,
        CancellationToken ct = default)
    {
        if (_store is null || !_byTask.TryGetValue(taskId, out var current)) return;
        if (current.State.IsTerminal()) return;
        try
        {
            // Stay in Working (legal self-transition) while renewing the lease.
            var next = current.Transition(
                node: NodeId.Coding,
                to: NodeState.Working,
                decision: decision,
                confidence: Confidence.Of(confidenceScore, confidenceNote),
                success: success,
                error: error,
                artifacts: artifacts,
                leaseFor: LeaseDuration);

            await _store.SavePacketAsync(next, ct);
            _byTask[taskId] = next;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Node packet record failed for task {TaskId} (non-fatal).", taskId);
        }
    }

    /// <summary>
    /// Drive the packet to a terminal state derived from the coding task's final status string.
    /// Idempotent — a no-op if already terminal or unknown.
    /// </summary>
    public async Task CompleteAsync(string taskId, string codingStatus, CancellationToken ct = default)
    {
        if (_store is null || !_byTask.TryRemove(taskId, out var current)) return;
        if (current.State.IsTerminal()) return;
        try
        {
            var to = MapStatus(codingStatus);
            var done = current.Transition(
                node: NodeId.Coding,
                to: to,
                decision: $"Coding loop finished with status '{codingStatus}'",
                confidence: current.LastEntry?.Confidence ?? Confidence.Unassessed,
                success: to == NodeState.Succeeded);

            await _store.SavePacketAsync(done, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Node packet complete failed for task {TaskId} (non-fatal).", taskId);
        }
    }

    /// <summary>Force the packet to Aborted (loop crashed). Mirrors the task-level abort guarantee.</summary>
    public async Task AbortAsync(string taskId, string reason, CancellationToken ct = default)
    {
        if (_store is null || !_byTask.TryRemove(taskId, out var current)) return;
        if (current.State.IsTerminal()) return;
        try
        {
            var aborted = current.Transition(
                node: NodeId.Coding,
                to: NodeState.Aborted,
                decision: "Coding loop aborted",
                success: false,
                error: reason);

            await _store.SavePacketAsync(aborted, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Node packet abort failed for task {TaskId} (non-fatal).", taskId);
        }
    }

    /// <summary>Maps the coding loop's free-text status to a terminal node state.</summary>
    private static NodeState MapStatus(string codingStatus) => codingStatus switch
    {
        "completed" => NodeState.Succeeded,
        "verification-failed" or "failed" or "no-op" or "blocked" => NodeState.Failed,
        _ => NodeState.Failed, // unknown terminal coding status is, conservatively, not a success
    };
}
