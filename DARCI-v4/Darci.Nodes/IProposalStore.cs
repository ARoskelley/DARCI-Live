#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Durable store of proposals awaiting a human decision (SQLite, consistent with the packet/gap/innovated
/// stores). Survives restart so a parked proposal is never lost, and lets the watchdog recognise a
/// legitimately-parked packet (one with a live pending proposal) rather than reaping it.
/// </summary>
public interface IProposalStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task AddAsync(HumanProposal proposal, CancellationToken ct = default);
    Task<HumanProposal?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<HumanProposal>> GetPendingAsync(int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<HumanProposal>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default);

    /// <summary>Record a human decision (status + who + note + when). No-op if already decided.</summary>
    Task<bool> RecordDecisionAsync(string id, HumanProposalStatus status, string? decidedBy, string? note, CancellationToken ct = default);

    /// <summary>Whether a packet parked in AwaitingDependency has a live pending proposal (watchdog carve-out).</summary>
    Task<bool> HasPendingForParkedPacketAsync(string parkedPacketId, CancellationToken ct = default);
}
