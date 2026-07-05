#nullable enable

namespace Darci.Nodes;

/// <summary>What a human is being asked to decide. Extensible; Phase C ships promotion of an innovated
/// hypothesis (the one path that may lift trust above the cap). Campaign authorization etc. are later.</summary>
public enum HumanProposalKind
{
    PromoteInnovated = 0,   // raise an innovated entry's provenance above the cap (governing invariant §0a)
}

public enum HumanProposalStatus { Pending = 0, Approved = 1, Rejected = 2 }

/// <summary>
/// A durable request for a human decision (the human gate, §9). References the originating packet chain
/// (CorrelationId), the subject it concerns (e.g. an innovated entry), the case/justification gathered so
/// far, and — optionally — a packet that is PARKED awaiting this decision. Nothing changes state until a
/// human decides; approval is what writes the privileged ledger event.
/// </summary>
public sealed record HumanProposal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string CorrelationId { get; init; } = "";
    public HumanProposalKind Kind { get; init; }

    /// <summary>The thing being decided on (e.g. an InnovatedKnowledgeRecord id).</summary>
    public string SubjectId { get; init; } = "";
    /// <summary>For promotion: the provenance the human is being asked to grant.</summary>
    public Provenance TargetProvenance { get; init; } = Provenance.HumanApproved;

    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    /// <summary>The case for the decision — evidence/ledger snapshot as JSON (justification package).</summary>
    public string JustificationJson { get; init; } = "";

    public HumanProposalStatus Status { get; init; } = HumanProposalStatus.Pending;

    /// <summary>A packet parked in AwaitingDependency awaiting this decision (null if nothing is blocked).</summary>
    public string? ParkedPacketId { get; init; }

    public string? DecisionNote { get; init; }
    public string? DecidedBy { get; init; }
    public DateTime? DecidedAt { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
