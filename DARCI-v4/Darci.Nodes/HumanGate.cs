#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>Outcome of submitting a human decision.</summary>
public sealed record HumanDecisionResult(bool Applied, string Message, HumanProposal? Proposal = null);

/// <summary>
/// The human gate: surfaces pending proposals and applies a human's decision. A UI/FRIDAY channel calls
/// this; the channel itself is separate. Approval is the ONE path that writes a human-authored ledger
/// event (§14a) — which is exactly what the Phase A invariant guard requires to lift an innovated entry
/// above the cap. Rejection is logged and the entry stays capped. Any parked packet resumes.
/// </summary>
public interface IHumanGate
{
    Task<IReadOnlyList<HumanProposal>> ListPendingAsync(int limit = 100, CancellationToken ct = default);
    Task<HumanDecisionResult> DecideAsync(string proposalId, bool approve, string? note, string? decidedBy, CancellationToken ct = default);
}

public sealed class HumanGateService : IHumanGate
{
    private readonly IProposalStore _proposals;
    private readonly IInnovatedKnowledgeStore _innovated;
    private readonly INodePacketStore _packets;
    private readonly ILogger<HumanGateService> _logger;

    public HumanGateService(
        IProposalStore proposals,
        IInnovatedKnowledgeStore innovated,
        INodePacketStore packets,
        ILogger<HumanGateService> logger)
    {
        _proposals = proposals;
        _innovated = innovated;
        _packets = packets;
        _logger = logger;
    }

    public Task<IReadOnlyList<HumanProposal>> ListPendingAsync(int limit = 100, CancellationToken ct = default)
        => _proposals.GetPendingAsync(limit, ct);

    public async Task<HumanDecisionResult> DecideAsync(string proposalId, bool approve, string? note, string? decidedBy, CancellationToken ct = default)
    {
        var proposal = await _proposals.GetAsync(proposalId, ct);
        if (proposal is null) return new HumanDecisionResult(false, "Proposal not found.");
        if (proposal.Status != HumanProposalStatus.Pending)
            return new HumanDecisionResult(false, $"Proposal already {proposal.Status}.", proposal);

        var status = approve ? HumanProposalStatus.Approved : HumanProposalStatus.Rejected;
        if (!await _proposals.RecordDecisionAsync(proposalId, status, decidedBy, note, ct))
            return new HumanDecisionResult(false, "Proposal was decided concurrently.", proposal);

        var who = decidedBy ?? "human";

        if (proposal.Kind == HumanProposalKind.PromoteInnovated)
            await ApplyPromotionDecisionAsync(proposal, approve, note, who, ct);

        await ResumeParkedPacketAsync(proposal, approve, ct);

        _logger.LogInformation("Human {Decision} proposal {Id} ({Kind}) by {By}.",
            approve ? "APPROVED" : "REJECTED", proposalId, proposal.Kind, who);

        return new HumanDecisionResult(true, approve ? "Approved." : "Rejected.",
            proposal with { Status = status, DecidedBy = decidedBy, DecisionNote = note, DecidedAt = DateTime.UtcNow });
    }

    private async Task ApplyPromotionDecisionAsync(HumanProposal proposal, bool approve, string? note, string who, CancellationToken ct)
    {
        var entry = await _innovated.GetAsync(proposal.SubjectId, ct);
        if (entry is null)
        {
            _logger.LogWarning("Promotion subject {Id} not found; decision recorded only.", proposal.SubjectId);
            return;
        }

        if (approve)
        {
            // The ONE above-cap path. Human-authored kind → the invariant guard permits raising trust.
            var (successes, _) = await _innovated.CountDistinctOutcomesAsync(entry.Id, ct);
            var promotedScore = Math.Clamp(0.6 + 0.05 * successes, 0.6, 0.9);   // trusted tier, above the cap
            await _innovated.UpdateAsync(
                entry with { Provenance = proposal.TargetProvenance, Confidence = Confidence.Of(promotedScore) },
                new LedgerEvent(LedgerEventKind.HumanConfirmPromotion,
                    $"human promoted {entry.Provenance} → {proposal.TargetProvenance} by {who}", Note: note),
                ct);
            _logger.LogInformation("Innovated {Id} promoted to {Prov} (conf {Score:0.##}) by human.",
                entry.Id, proposal.TargetProvenance, promotedScore);
        }
        else
        {
            // Rejection: log it; the entry stays capped (Innovated). Downward/same → guard trivially allows.
            await _innovated.UpdateAsync(
                entry,
                new LedgerEvent(LedgerEventKind.HumanReject, $"human rejected promotion by {who}", Note: note),
                ct);
        }
    }

    private async Task ResumeParkedPacketAsync(HumanProposal proposal, bool approve, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(proposal.ParkedPacketId)) return;
        try
        {
            var packet = await _packets.GetPacketAsync(proposal.ParkedPacketId!, ct);
            if (packet is null || packet.State != NodeState.AwaitingDependency) return;

            // Phase C: the human dependency is resolved, so the parked packet reaches a terminal state.
            // (Resuming a suspended sub-loop to CONTINUE work is Phase D/E.)
            var resumed = packet.Transition(NodeId.Orchestrator,
                approve ? NodeState.Succeeded : NodeState.Failed,
                $"Resumed on human {(approve ? "approval" : "rejection")}.",
                success: approve);
            await _packets.SavePacketAsync(resumed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resume parked packet {Id} (non-fatal).", proposal.ParkedPacketId);
        }
    }
}
