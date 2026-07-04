#nullable enable

namespace Darci.Nodes;

/// <summary>
/// The "kind" axis of a piece of knowledge, orthogonal to <see cref="Confidence"/>. Innovated
/// (synthesized) knowledge must never be looked up later as established fact, so it carries a distinct
/// provenance and a hard confidence cap until it earns its way up the staged lifecycle (see the
/// promotion pathway, §4a of docs/INNOVATION_NODE_DESIGN.md).
/// </summary>
public enum Provenance
{
    Verified = 0,               // ground truth / established fact — trusted
    Researched = 1,             // from deep research — trusted
    Innovated = 2,              // freshly synthesized hypothesis, untested — capped
    UnderTest = 3,              // being empirically exercised — capped
    ProvisionallyValidated = 4, // N successful real-world uses; bounded automatic uplift — capped (automatic ceiling)
    HumanApproved = 5,          // human sign-off (above-cap promotion pathway — design only for now)
    Unverified = 6,             // low-trust / unknown — capped
    Retracted = 7,              // failed empirically — excluded from lookups
}

/// <summary>
/// The single enforcement point for provenance-aware trust. Innovated/lifecycle provenances are
/// clamped to the cap so a hypothesis can never present as fact; the outcome loop advances the stage
/// but never above the cap (only the human-confirmation tier, design-only, may exceed it).
/// </summary>
public static class ProvenancePolicy
{
    /// <summary>
    /// Hard ceiling on confidence for un-human-confirmed innovated knowledge. Tinman's bound is ≤0.4;
    /// set at 0.35, strictly below <see cref="Confidence.LowThreshold"/> (0.4, which <see cref="Confidence.IsLow"/>
    /// tests with `&lt;`), so a capped entry is ALWAYS IsLow — it can never read as a confident fact.
    /// </summary>
    public const double InnovatedCap = 0.35;

    /// <summary>May be surfaced to a consumer as an established fact.</summary>
    public static bool IsTrustedAsFact(Provenance p) => p is Provenance.Verified or Provenance.Researched;

    /// <summary>The innovation lifecycle stages the automatic outcome loop moves through.</summary>
    public static bool IsInnovationLifecycle(Provenance p) =>
        p is Provenance.Innovated or Provenance.UnderTest or Provenance.ProvisionallyValidated;

    /// <summary>Subject to the confidence cap (never presents as fact).</summary>
    public static bool IsCapped(Provenance p) => IsInnovationLifecycle(p) || p == Provenance.Unverified;

    /// <summary>Excluded from lookups entirely (kept only for audit/revert).</summary>
    public static bool IsExcluded(Provenance p) => p == Provenance.Retracted;

    /// <summary>Clamp a confidence to what its provenance is allowed to claim.</summary>
    public static Confidence Clamp(Provenance p, Confidence c)
    {
        if (IsExcluded(p)) return Confidence.Unassessed;
        if (IsCapped(p) && c.IsAssessed && c.Score > InnovatedCap)
            return Confidence.Of(InnovatedCap, c.Note);
        return c;
    }

    /// <summary>
    /// Trust ordering. An UPWARD move (higher rank) is a behavioral boundary crossing and may only
    /// happen via a human-authored ledger event (governing invariant §0a). Automatic processing may
    /// only keep rank equal or lower it.
    /// </summary>
    public static int TrustRank(Provenance p) => p switch
    {
        Provenance.Retracted => 0,
        Provenance.Unverified => 1,
        Provenance.Innovated => 2,
        Provenance.UnderTest => 3,
        Provenance.ProvisionallyValidated => 4,
        Provenance.HumanApproved => 5,
        Provenance.Researched => 6,
        Provenance.Verified => 7,
        _ => 0,
    };

    /// <summary>
    /// The automatic downward step taken on failure: one stage down; from the bottom innovation stage
    /// (Innovated) or Unverified, straight to Retracted. Trusted external kinds are left untouched.
    /// </summary>
    public static Provenance DemoteOneStage(Provenance p) => p switch
    {
        Provenance.HumanApproved => Provenance.ProvisionallyValidated,
        Provenance.ProvisionallyValidated => Provenance.UnderTest,
        Provenance.UnderTest => Provenance.Innovated,
        Provenance.Innovated => Provenance.Retracted,
        Provenance.Unverified => Provenance.Retracted,
        _ => p,
    };
}
