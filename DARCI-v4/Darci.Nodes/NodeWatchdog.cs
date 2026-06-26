#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// Reaps orphaned packets — the structural fix for the task-orphaning bug. A packet can only be
/// "stuck" if it is active (non-terminal) while nothing is working it. Two ways that happens:
///   1. A node hung or crashed mid-work: its lease lapses → <see cref="SweepExpiredLeasesAsync"/>.
///   2. The whole process died and restarted: any still-active packet has no live owner →
///      <see cref="SweepStartupOrphansAsync"/> aborts them on boot.
/// Both transition the packet to <see cref="NodeState.Aborted"/> with an explanatory log entry, so a
/// non-terminal packet can never survive indefinitely without a live lease.
/// </summary>
public sealed class NodeWatchdog
{
    private readonly INodePacketStore _store;
    private readonly ILogger<NodeWatchdog> _logger;

    public NodeWatchdog(INodePacketStore store, ILogger<NodeWatchdog> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Abort every active packet whose lease has expired as of <paramref name="nowUtc"/>.
    /// Returns the number reaped.
    /// </summary>
    public async Task<int> SweepExpiredLeasesAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var stale = await _store.GetActivePacketsWithExpiredLeaseAsync(nowUtc, ct);
        var reaped = 0;
        foreach (var packet in stale)
        {
            if (await AbortAsync(packet,
                    $"Lease expired at {packet.LeaseExpiresAt:O}; node {Owner(packet)} is not responding.",
                    nowUtc, ct))
                reaped++;
        }

        if (reaped > 0)
            _logger.LogWarning("Watchdog reaped {Count} packet(s) with expired leases.", reaped);

        return reaped;
    }

    /// <summary>
    /// On process startup, abort any packet left active by a previous run. Nothing is working them in
    /// this fresh process, so they are orphans by definition. Returns the number reaped.
    /// </summary>
    public async Task<int> SweepStartupOrphansAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var active = await _store.GetActivePacketsAsync(ct);
        var reaped = 0;
        foreach (var packet in active)
        {
            if (await AbortAsync(packet,
                    "Process restarted while packet was active; no live owner — reaped on startup.",
                    now, ct))
                reaped++;
        }

        if (reaped > 0)
            _logger.LogWarning("Watchdog reaped {Count} orphaned packet(s) on startup.", reaped);

        return reaped;
    }

    private async Task<bool> AbortAsync(NodePacket packet, string reason, DateTime nowUtc, CancellationToken ct)
    {
        if (packet.State.IsTerminal()) return false; // raced to terminal — leave it.
        try
        {
            var aborted = packet.Transition(
                node: packet.Address ?? NodeId.Orchestrator,
                to: NodeState.Aborted,
                decision: "Watchdog abort",
                confidence: Confidence.Unassessed,
                success: false,
                error: reason,
                nowUtc: nowUtc);

            await _store.SavePacketAsync(aborted, ct);
            _logger.LogWarning("Aborted packet {Id} (was {State}): {Reason}", packet.Id, packet.State, reason);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Watchdog failed to abort packet {Id}.", packet.Id);
            return false;
        }
    }

    private static NodeId Owner(NodePacket p) => p.Address ?? NodeId.Orchestrator;
}
