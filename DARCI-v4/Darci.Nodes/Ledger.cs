#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Kinds of events in an innovated entry's append-only ledger. Split by AUTHORSHIP: automatic kinds may
/// only append evidence or move an entry DOWN in trust; human-authored kinds are the only ones allowed
/// to cross a behavioral boundary upward (governing invariant §0a). Human kinds are appendable only via
/// the UI-node gate (design-only until Phase C) — but the split is enforced now by the store guard.
/// </summary>
public enum LedgerEventKind
{
    Created = 0,
    SuccessEvidence = 1,   // automatic — appends evidence
    FailureEvidence = 2,   // automatic — appends evidence (+ demotion)
    AutoDemotion = 3,      // automatic — moves down

    // Human-authored (privileged): the ONLY kinds permitted to raise trust.
    HumanAuthorizeCampaign = 100,
    HumanConfirmPromotion = 101,
    HumanReject = 102,
    HumanRetract = 103,
}

public static class Ledger
{
    /// <summary>Whether a kind is human-authored and thus permitted to cross an upward boundary.</summary>
    public static bool IsHumanAuthored(LedgerEventKind k) => (int)k >= 100;
}

/// <summary>
/// A structured ledger event driving a state change on an innovated entry. Carries the machine-readable
/// fields (kind, correlation root, weight, campaign id) — the human-readable <see cref="Change"/> is a
/// derived label, not the source of truth.
/// </summary>
public sealed record LedgerEvent(
    LedgerEventKind Kind,
    string Change,
    string? CorrelationRoot = null,
    double Weight = 1.0,
    string? CampaignId = null,
    string? Note = null);
