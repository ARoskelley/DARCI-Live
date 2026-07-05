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
    /// request, and park <paramref name="parentPacket"/> awaiting the decision. Nothing on the entry
    /// changes yet — this only PROPOSES. Sensitive domains never pre-authorize the promotion touch.
    /// </summary>
    Task<ValidationCampaign> DraftAndRequestAuthorizationAsync(
        InnovatedKnowledgeRecord entry,
        IReadOnlyList<ValidationStep> protocol,
        Provenance targetStage,
        KnowledgeDomain domain,
        NodePacket parentPacket,
        bool preauthorizePromotion = false,
        CancellationToken ct = default);

    /// <summary>Apply a human decision on an <see cref="HumanProposalKind.AuthorizeCampaign"/> proposal:
    /// on approve, write the HumanAuthorizeCampaign event, run the steps, compute the verdict, and file (or
    /// pre-authorized: apply) the promotion. On reject, close the campaign.</summary>
    Task HandleAuthorizationDecisionAsync(HumanProposal proposal, bool approve, string decidedBy, CancellationToken ct = default);

    /// <summary>Apply a human decision on a <see cref="HumanProposalKind.PromoteFromCampaign"/> proposal
    /// (the 2nd touch): on approve, promote the entry to the campaign's target stage (domain-capped).</summary>
    Task HandlePromotionDecisionAsync(HumanProposal proposal, bool approve, string decidedBy, CancellationToken ct = default);
}
