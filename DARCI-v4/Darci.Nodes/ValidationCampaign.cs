#nullable enable

namespace Darci.Nodes;

/// <summary>How a validation step gathers its evidence — each routes to a different environment/capability.</summary>
public enum ValidationStepKind
{
    SandboxTest = 0,            // coding node: compile / dry-run / test in a sandbox
    Simulation = 1,            // a simulation environment (may not exist yet → tooling proposal)
    LiveObservation = 2,       // observed real deployment / usage
    ExternalResearchCheck = 3, // deep-research corroboration against external sources
}

/// <summary>Outcome of a single step. Blocked = the environment to run it does not exist yet (§14c).</summary>
public enum ValidationStepOutcome
{
    Pending = 0,
    Passed = 1,
    Failed = 2,
    Blocked = 3,
}

/// <summary>Lifecycle of a campaign as it rides the human gate + child-packet machinery (sub-unit 2).</summary>
public enum CampaignStatus
{
    Draft = 0,                 // sketched by the innovation node / human, not yet submitted
    AwaitingAuthorization = 1, // authorization request filed to ProposalStore; parent packet parked
    Authorized = 2,            // human approved the DESIGN (HumanAuthorizeCampaign); steps may run
    Running = 3,               // child step packets in flight
    AwaitingPromotion = 4,     // verdict computed; promotion proposal filed (2nd human touch)
    Blocked = 5,               // a step needs an environment that doesn't exist → parked on tooling/gap
    Completed = 6,             // terminal — promoted (or verdict accepted and closed)
    Rejected = 7,              // terminal — human rejected the design or the promotion
    Abandoned = 8,             // terminal — budget exhausted / superseded
}

/// <summary>Scheduling priority of a campaign's work. Human-initiated (or explicitly-requested) campaigns
/// always outrank auto-drafted ones. Higher numeric value = higher priority (so <see cref="IWorkScheduler"/>
/// serves HumanInitiated before AutoDrafted). A future resource-allocation scheduler may widen this to a
/// richer signal, but callers only depend on the ordering.</summary>
public enum CampaignPriority
{
    AutoDrafted = 0,     // produced by the eligibility sweep — lowest priority
    HumanInitiated = 1,  // requested by a human / explicit code path — highest priority
}

/// <summary>Lifecycle helpers.</summary>
public static class CampaignLifecycle
{
    /// <summary>True while a campaign is still "live" (not a terminal outcome) — used to de-dupe so the
    /// sweep never auto-drafts a second campaign for an entry that already has one in flight.</summary>
    public static bool IsActive(this CampaignStatus s) => s is not (
        CampaignStatus.Completed or CampaignStatus.Rejected or CampaignStatus.Abandoned);
}

/// <summary>The verdict computed mechanically over the pre-registered criteria × step evidence.</summary>
public enum CampaignVerdict
{
    Pending = 0,        // not all steps have evidence yet
    Passed = 1,         // every step met its pre-registered criteria
    Failed = 2,         // a step failed or missed its criteria
    Inconclusive = 3,   // a step is Blocked (no environment) — cannot conclude
}

/// <summary>Comparison operator for a pre-registered success criterion.</summary>
public enum Comparator { GreaterOrEqual = 0, LessOrEqual = 1, Equal = 2 }

/// <summary>
/// A PRE-REGISTERED, objective success criterion, fixed BEFORE the campaign runs and never edited after.
/// Evaluated mechanically against a step's measured values — no post-hoc "it sort of worked".
/// </summary>
public sealed record SuccessCriteria(string Metric, Comparator Comparator, double Threshold, string? Description = null)
{
    /// <summary>Whether the step actually produced a measurement for this criterion's metric. Absence means
    /// the metric was NOT measured (the protocol couldn't test this way) — distinct from a measured miss.</summary>
    public bool HasMetric(IReadOnlyDictionary<string, double> measurements)
        => measurements is not null && measurements.ContainsKey(Metric);

    /// <summary>Pure predicate: is this criterion met by the step's measurements? Absent metric ⇒ not met
    /// (callers that must distinguish "absent" from "measured below bar" use <see cref="HasMetric"/> first).</summary>
    public bool IsMetBy(IReadOnlyDictionary<string, double> measurements)
    {
        if (measurements is null || !measurements.TryGetValue(Metric, out var v)) return false;
        return Comparator switch
        {
            Comparator.GreaterOrEqual => v >= Threshold,
            Comparator.LessOrEqual => v <= Threshold,
            Comparator.Equal => Math.Abs(v - Threshold) < 1e-9,
            _ => false,
        };
    }
}

/// <summary>
/// One pre-registered step of a validation protocol. It becomes a CHILD PACKET routed by
/// <see cref="Capability"/> to <see cref="Environment"/>. Its <see cref="Criteria"/> are pinned before the
/// campaign is authorized and are immutable thereafter.
/// </summary>
public sealed record ValidationStep(
    string Id,
    ValidationStepKind Kind,
    Capability Capability,
    NodeId Environment,
    SuccessCriteria Criteria,
    string Description = "",
    int Budget = 1);

/// <summary>The evidence a step produced — the measured values the pre-registered criteria are checked against.</summary>
public sealed record StepEvidence(
    string StepId,
    ValidationStepOutcome Outcome,
    IReadOnlyDictionary<string, double> Measurements,
    string? Note = null,
    string? ChildPacketId = null,
    DateTime At = default);

/// <summary>The human authorization of a campaign DESIGN (not its verdict): who, the approved budget, and
/// whether the second (promotion) touch was pre-authorized. General domains MAY pre-authorize; sensitive
/// domains never do (both touches mandatory).</summary>
public sealed record CampaignAuthorization(
    string ApprovedBy,
    int ApprovedBudget,
    DateTime AuthorizedAt,
    bool PromotionPreauthorized = false);

/// <summary>
/// A first-class validation campaign (§14b): the antidote to validation theater. It pins an immutable
/// snapshot of exactly what is under test (<see cref="EntryId"/> + <see cref="HypothesisRevisionSeq"/> +
/// <see cref="HypothesisSnapshot"/>), a PRE-REGISTERED <see cref="Protocol"/> of steps each with fixed
/// success criteria, the promotion it seeks (<see cref="TargetStage"/>), the human authorization, and a
/// verdict computed as a PURE FUNCTION (<see cref="CampaignProtocol.Evaluate"/>). Sensitive-domain
/// campaigns never auto-promote.
/// </summary>
public sealed record ValidationCampaign
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The innovated entry under test.</summary>
    public string EntryId { get; init; } = "";
    /// <summary>Immutable snapshot pointer: the ledger revision seq of exactly what is being validated.</summary>
    public int HypothesisRevisionSeq { get; init; }
    /// <summary>The hypothesis text as it was when the campaign was drafted (self-contained snapshot).</summary>
    public string HypothesisSnapshot { get; init; } = "";

    /// <summary>The promotion this campaign seeks (e.g. ProvisionallyValidated, then HumanApproved).</summary>
    public Provenance TargetStage { get; init; } = Provenance.ProvisionallyValidated;
    public KnowledgeDomain Domain { get; init; } = KnowledgeDomain.General;

    /// <summary>PRE-REGISTERED protocol — fixed before running.</summary>
    public IReadOnlyList<ValidationStep> Protocol { get; init; } = Array.Empty<ValidationStep>();

    /// <summary>Null until a human authorizes the DESIGN.</summary>
    public CampaignAuthorization? Authorization { get; init; }

    public CampaignStatus Status { get; init; } = CampaignStatus.Draft;

    /// <summary>Parent packet correlation — child step packets run under this root.</summary>
    public string CorrelationId { get; init; } = "";

    /// <summary>Whether the second (promotion) human touch was pre-authorized at design time. Never true for
    /// <see cref="KnowledgeDomain.Sensitive"/>.</summary>
    public bool PromotionPreauthorized { get; init; }

    /// <summary>Scheduling priority. Human-initiated campaigns outrank auto-drafted ones.</summary>
    public CampaignPriority Priority { get; init; } = CampaignPriority.HumanInitiated;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// The mechanical verdict function (§14b). PURE: <c>verdict = f(pre-registered criteria × step evidence)</c>.
/// No campaign state, wall-clock, or model opinion enters — same inputs always yield the same verdict, so a
/// passing verdict cannot be rationalized after the fact.
/// </summary>
public static class CampaignProtocol
{
    /// <summary>Fold the protocol against the collected evidence into a single verdict.</summary>
    public static CampaignVerdict Evaluate(IReadOnlyList<ValidationStep> protocol, IReadOnlyList<StepEvidence> evidence)
    {
        if (protocol is null || protocol.Count == 0) return CampaignVerdict.Pending;

        var byStep = new Dictionary<string, StepEvidence>();
        foreach (var e in evidence) byStep[e.StepId] = e;   // last write wins per step

        var anyBlocked = false;
        var anyPending = false;
        var anyUnmeasured = false;

        foreach (var step in protocol)
        {
            if (!byStep.TryGetValue(step.Id, out var ev) || ev.Outcome == ValidationStepOutcome.Pending)
            {
                anyPending = true;
                continue;
            }
            if (ev.Outcome == ValidationStepOutcome.Blocked) { anyBlocked = true; continue; }
            if (ev.Outcome == ValidationStepOutcome.Failed) return CampaignVerdict.Failed;

            // Passed outcome must ALSO meet the pre-registered criteria — but distinguish two cases:
            //   metric ABSENT   → the protocol couldn't measure this ⇒ INCONCLUSIVE (never a failure/demotion);
            //   metric PRESENT but below bar → a genuine measured miss ⇒ Failed.
            if (!step.Criteria.HasMetric(ev.Measurements)) { anyUnmeasured = true; continue; }
            if (!step.Criteria.IsMetBy(ev.Measurements)) return CampaignVerdict.Failed;
        }

        // A missing environment or an unmeasured metric both mean "cannot conclude" — not a failure.
        if (anyBlocked || anyUnmeasured) return CampaignVerdict.Inconclusive;
        if (anyPending) return CampaignVerdict.Pending;
        return CampaignVerdict.Passed;
    }

    /// <summary>Convenience overload evaluating a whole campaign.</summary>
    public static CampaignVerdict Evaluate(ValidationCampaign campaign, IReadOnlyList<StepEvidence> evidence)
        => Evaluate(campaign.Protocol, evidence);
}
