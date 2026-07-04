#nullable enable

namespace Darci.Nodes;

/// <summary>
/// A persisted innovated-knowledge entry: a synthesized hypothesis and its lifecycle. Kept in SQLite
/// with an append-only revision log so an entry can be edited/reverted if it later fails, and enough
/// context (hypothesis, originating intent, cited facts, correlation) to trace and learn from.
/// Confidence is always stored clamped by <see cref="ProvenancePolicy"/>.
/// </summary>
public sealed record InnovatedKnowledgeRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Ties the entry back to the originating packet chain and forward to the coding outcome.</summary>
    public string CorrelationId { get; init; } = "";
    public string OriginPacketId { get; init; } = "";

    /// <summary>The synthesized claim/solution.</summary>
    public string Hypothesis { get; init; } = "";
    /// <summary>The question/topic it answers.</summary>
    public string Topic { get; init; } = "";
    /// <summary>The originating goal/intent (raw material for later theorizing).</summary>
    public string Intent { get; init; } = "";
    /// <summary>KG fact ids the hypothesis is grounded in.</summary>
    public IReadOnlyList<string> CitedFactIds { get; init; } = Array.Empty<string>();

    public Provenance Provenance { get; init; } = Provenance.Innovated;
    public Confidence Confidence { get; init; } = Confidence.Unassessed;

    /// <summary>Empirical evidence accumulated via the outcome loop.</summary>
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// One append-only entry in an innovated record's ledger (audit + evidence). Structured: the machine
/// fields (Kind, CorrelationRoot, Weight, CampaignId) are authoritative; Change is a derived label.
/// State(entry) = fold(ledger); Phase A materializes it, but the ledger is the source of truth.
/// </summary>
public sealed record InnovatedRevision
{
    public string EntryId { get; init; } = "";
    public int Seq { get; init; }
    public DateTime At { get; init; } = DateTime.UtcNow;
    public LedgerEventKind Kind { get; init; }
    public string Change { get; init; } = "";
    public Provenance Provenance { get; init; }
    public Confidence Confidence { get; init; } = Confidence.Unassessed;
    public string? CorrelationRoot { get; init; }
    public double Weight { get; init; } = 1.0;
    public string? CampaignId { get; init; }
    public string? Note { get; init; }
}

/// <summary>Resolution state of a consumption link.</summary>
public enum ConsumptionOutcome { Pending = 0, Success = 1, Failure = 2 }

/// <summary>
/// Records that an innovated entry was SERVED into work identified by a correlation root — the fix for
/// the correlation-matching bug. Outcomes are matched to entries via these links, and evidence is
/// counted by DISTINCT root, so retries of the same task collapse to one and independent uses count
/// separately. Keyed by (EntryId, CorrelationRoot).
/// </summary>
public sealed record InnovatedConsumption
{
    public string EntryId { get; init; } = "";
    public string CorrelationRoot { get; init; } = "";
    public ConsumptionOutcome Outcome { get; init; } = ConsumptionOutcome.Pending;
    public double Weight { get; init; } = 1.0;
    public string? CampaignId { get; init; }
    public DateTime ServedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; init; }
}
