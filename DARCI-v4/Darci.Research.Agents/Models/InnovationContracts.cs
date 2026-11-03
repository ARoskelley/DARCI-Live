#nullable enable

using Darci.Nodes;

namespace Darci.Research.Agents.Models;

/// <summary>Status of a single-pass innovation proposal (Phase B subset — campaign/human states are later).</summary>
public enum ProposalStatus
{
    Proposed,          // synthesized, not yet internally vetted
    VettedInternally,  // passed the (separate) plausibility reviewer
    Unsolvable,        // honest "no synthesis of known info solves this; needs external data/experiment X"
}

/// <summary>One reasoning step of a proposal, and the facts it claims to draw on.</summary>
public sealed record ReasoningLink(string Inference, IReadOnlyList<string> CitedFacts);

/// <summary>
/// Rigid input to the innovation node. KGMA fills this from the pipeline's failure: the unmet question,
/// the residual gaps, and the KG/DR substrate already gathered (the material to recombine).
/// </summary>
public sealed record InnovationRequest(
    string Question,
    string Intent = "",
    string? FailureContext = null,
    IReadOnlyList<string>? Gaps = null,
    IReadOnlyList<string>? KnownFacts = null)
{
    public IReadOnlyList<string> GapList => Gaps ?? Array.Empty<string>();
    public IReadOnlyList<string> FactList => KnownFacts ?? Array.Empty<string>();
}

/// <summary>
/// Structured single-pass innovation proposal. Always low-trust: <see cref="Provenance"/> is Innovated and
/// <see cref="Confidence"/> is capped (always IsLow) — a hypothesis, never a fact. The
/// <see cref="Plausibility"/> verdict comes from a SEPARATE reviewer (generator ≠ evaluator), even in
/// single-pass. Honest <see cref="ProposalStatus.Unsolvable"/> with <see cref="RequiredExternalInputs"/>
/// is a first-class output.
/// </summary>
public sealed record InnovationProposal
{
    public ProposalStatus Status { get; init; }
    public string Hypothesis { get; init; } = "";
    public IReadOnlyList<ReasoningLink> Reasoning { get; init; } = Array.Empty<ReasoningLink>();
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiredExternalInputs { get; init; } = Array.Empty<string>();
    public Confidence Confidence { get; init; } = Confidence.Unassessed;
    public Provenance Provenance { get; init; } = Provenance.Innovated;

    /// <summary>The independent plausibility reviewer's verdict (null until reviewed / for Unsolvable).</summary>
    public KnowledgeReview? Plausibility { get; init; }

    public string CorrelationId { get; init; } = "";

    public static InnovationProposal CannotSolve(string reason, IReadOnlyList<string> requiredExternalInputs) => new()
    {
        Status = ProposalStatus.Unsolvable,
        RequiredExternalInputs = requiredExternalInputs,
        Confidence = Confidence.Unassessed,
        Assumptions = new[] { reason },
    };
}
