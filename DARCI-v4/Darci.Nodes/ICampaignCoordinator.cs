#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Orchestrates a validation campaign across the existing machinery (§14b): drafts it with a
/// critic-falsified protocol, files the authorization request into the ProposalStore + parks the parent
/// packet, and — once a human authorizes the DESIGN — writes the human-authored ledger event, runs each
/// pre-registered step as a CHILD PACKET under the parent correlation, records step evidence, computes the
/// mechanical verdict, and files the promotion proposal (the 2nd human touch). The interface lives in
/// Darci.Nodes so the human gate can delegate campaign decisions to it; the implementation (which needs the
/// LLM protocol critic) lives in Darci.Research.Agents.
/// </summary>
public interface ICampaignCoordinator
{
    /// <summary>
    /// Draft a campaign for <paramref name="entry"/> with the given PRE-REGISTERED protocol, run the
    /// protocol critic (falsify the design), file an <see cref="HumanProposalKind.AuthorizeCampaign"/>
    /// request, and park a parent packet awaiting the decision. Nothing on the entry changes yet — this
    /// only PROPOSES (so an auto-drafted campaign still parks for human authorization exactly like a
    /// human-initiated one). Sensitive domains never pre-authorize the promotion touch.
    /// </summary>
    /// <param name="parentPacket">The in-flight packet to park (human-initiated). Pass null for an
    /// auto-drafted campaign with no live packet — the coordinator mints one to park.</param>
    /// <param name="priority">Human-initiated outranks auto-drafted in the work scheduler.</param>
    Task<ValidationCampaign> DraftAndRequestAuthorizationAsync(
        InnovatedKnowledgeRecord entry,
        IReadOnlyList<ValidationStep> protocol,
        Provenance targetStage,
        KnowledgeDomain domain,
        NodePacket? parentPacket = null,
        bool preauthorizePromotion = false,
        CampaignPriority priority = CampaignPriority.HumanInitiated,
        CancellationToken ct = default);

    /// <summary>Apply a human decision on an <see cref="HumanProposalKind.AuthorizeCampaign"/> proposal:
    /// on approve, write the HumanAuthorizeCampaign event, run the steps, compute the verdict, and file (or
    /// pre-authorized: apply) the promotion. On reject, close the campaign.</summary>
    Task HandleAuthorizationDecisionAsync(HumanProposal proposal, bool approve, string decidedBy, CancellationToken ct = default);

    /// <summary>Apply a human decision on a <see cref="HumanProposalKind.PromoteFromCampaign"/> proposal
    /// (the 2nd touch): on approve, promote the entry to the campaign's target stage (domain-capped).</summary>
    Task HandlePromotionDecisionAsync(HumanProposal proposal, bool approve, string decidedBy, CancellationToken ct = default);

    /// <summary>Re-drive a campaign that was parked (<see cref="CampaignStatus.Blocked"/>) because a step had
    /// no environment. Once a human has built + deployed the missing node (compile-time; the tooling
    /// landed), this re-runs the steps and finalizes. Returns true if the campaign advanced past Blocked.</summary>
    Task<bool> ResumeBlockedCampaignAsync(string campaignId, CancellationToken ct = default);
}
