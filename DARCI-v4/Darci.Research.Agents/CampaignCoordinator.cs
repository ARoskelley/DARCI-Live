#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>Whether an innovated entry has earned the right to have a campaign PROPOSED for it. Pure — it
/// never flips state (§14a: eligibility proposes, never promotes).</summary>
public sealed record CampaignEligibility(bool Eligible, string Reason);

public sealed class CampaignEligibilityOptions
{
    /// <summary>Distinct-root successes required before an entry is worth a (costly) campaign.</summary>
    public int MinDistinctSuccesses { get; init; } = 2;
    /// <summary>Above this many distinct failures the entry should be demoted, not campaigned.</summary>
    public int MaxFailures { get; init; } = 1;
}

/// <summary>Pure eligibility gate over the evidence ledger.</summary>
public static class CampaignEligibilityEvaluator
{
    public static CampaignEligibility Evaluate(InnovatedKnowledgeRecord entry, (int Successes, int Failures) outcomes, CampaignEligibilityOptions opt)
    {
        if (entry.Provenance != Provenance.Innovated)
            return new CampaignEligibility(false, $"not at the Innovated stage (is {entry.Provenance})");
        if (outcomes.Failures > opt.MaxFailures)
            return new CampaignEligibility(false, $"too many distinct failures ({outcomes.Failures} > {opt.MaxFailures})");
        if (outcomes.Successes < opt.MinDistinctSuccesses)
            return new CampaignEligibility(false, $"insufficient distinct successes ({outcomes.Successes}/{opt.MinDistinctSuccesses})");
        return new CampaignEligibility(true, $"{outcomes.Successes} distinct successes, {outcomes.Failures} failures — eligible to PROPOSE a campaign");
    }
}

/// <summary>
/// Drives a validation campaign through the existing machinery (§14b). Draft → critic-falsify the protocol →
/// authorization request in the ProposalStore (parent packet parked) → on authorize, HumanAuthorizeCampaign
/// ledger event (Innovated→UnderTest) + each step run as a CHILD PACKET routed by capability → mechanical
/// verdict → promotion proposal (2nd touch), or a pre-authorized apply for general domains. Sensitive
/// domains never auto-promote. A step whose environment does not exist parks the campaign on a
/// needs-external-input gap. Generator/critic/coordinator are distinct; trust only rises via human events.
/// </summary>
public sealed class CampaignCoordinator : ICampaignCoordinator
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IValidationCampaignStore _campaigns;
    private readonly IInnovatedKnowledgeStore _innovated;
    private readonly IProposalStore _proposals;
    private readonly INodeRouter _router;
    private readonly INodePacketStore _packets;
    private readonly IGapStore _gaps;
    private readonly IProtocolCritic _protocolCritic;
    private readonly IReadOnlyList<INode> _nodes;   // to know whether a step's environment EXISTS
    private readonly ISandboxPoCGate? _poc;         // sub-unit 3 — objective PoC before the human sees it
    private readonly ILogger<CampaignCoordinator> _logger;

    public CampaignCoordinator(
        IValidationCampaignStore campaigns,
        IInnovatedKnowledgeStore innovated,
        IProposalStore proposals,
        INodeRouter router,
        INodePacketStore packets,
        IGapStore gaps,
        IProtocolCritic protocolCritic,
        IEnumerable<INode> nodes,
        ILogger<CampaignCoordinator> logger,
        ISandboxPoCGate? poc = null)
    {
        _campaigns = campaigns;
        _innovated = innovated;
        _proposals = proposals;
        _router = router;
        _packets = packets;
        _gaps = gaps;
        _protocolCritic = protocolCritic;
        _nodes = nodes.ToList();
        _poc = poc;
        _logger = logger;
    }

    public async Task<ValidationCampaign> DraftAndRequestAuthorizationAsync(
        InnovatedKnowledgeRecord entry, IReadOnlyList<ValidationStep> protocol, Provenance targetStage,
        KnowledgeDomain domain, NodePacket parentPacket, bool preauthorizePromotion = false, CancellationToken ct = default)
    {
        // Sensitive domains NEVER pre-authorize the promotion touch — both human touches are mandatory.
        var preauth = preauthorizePromotion && domain == KnowledgeDomain.General;

        var revs = await _innovated.GetRevisionsAsync(entry.Id, ct);
        var snapshotSeq = revs.Count > 0 ? revs[^1].Seq : 0;

        var campaign = new ValidationCampaign
        {
            EntryId = entry.Id,
            HypothesisRevisionSeq = snapshotSeq,
            HypothesisSnapshot = entry.Hypothesis,
            TargetStage = targetStage,
            Domain = domain,
            Protocol = protocol,
            Status = CampaignStatus.AwaitingAuthorization,
            CorrelationId = string.IsNullOrEmpty(parentPacket.CorrelationId) ? parentPacket.Id : parentPacket.CorrelationId,
            PromotionPreauthorized = preauth,
        };

        // The critic falsifies the DESIGN before a human sees it — attached so the human approves the plan.
        var critique = await _protocolCritic.FalsifyAsync(campaign, ct);
        await _campaigns.AddAsync(campaign, ct);

        // Objective PoC (sub-unit 3): route the candidate to a sandbox build/dry-run and attach the result
        // as weight-capped evidence BEFORE the human sees the proposal. Best-effort — never blocks drafting.
        ProofOfConcept? poc = null;
        if (_poc is not null)
        {
            try { poc = await _poc.AttachAsync(entry, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Sandbox PoC failed during draft (non-fatal).");
            }
            revs = await _innovated.GetRevisionsAsync(entry.Id, ct);   // refresh so the case reflects the PoC
        }

        // Park the parent packet (clears the lease) — the campaign is an external dependency (§9).
        var parked = parentPacket.State == NodeState.AwaitingDependency
            ? parentPacket
            : parentPacket.ParkAwaitingDependency(NodeId.Innovation, $"awaiting authorization of campaign {campaign.Id}");
        await _packets.SavePacketAsync(parked, ct);

        var justification = JsonSerializer.Serialize(new
        {
            hypothesis = entry.Hypothesis,
            targetStage = targetStage.ToString(),
            domain = domain.ToString(),
            preauthorizePromotion = preauth,
            protocol,
            protocolCritique = critique,
            proofOfConcept = poc,
            ledgerSoFar = revs,
        }, JsonOpts);

        var proposal = new HumanProposal
        {
            CorrelationId = campaign.CorrelationId,
            Kind = HumanProposalKind.AuthorizeCampaign,
            SubjectId = campaign.Id,
            TargetProvenance = targetStage,
            Title = $"Authorize validation campaign: {Truncate(entry.Hypothesis, 70)}",
            Summary = critique.Adequate
                ? "Protocol judged adequate by the critic — approve the DESIGN to run it."
                : $"Protocol has unexercised failure modes ({critique.UnexercisedFailureModes.Count}) — review before authorizing.",
            JustificationJson = justification,
            ParkedPacketId = parked.Id,
        };
        await _proposals.AddAsync(proposal, ct);

        _logger.LogInformation("Drafted campaign {Id} for entry {Entry} (target {Stage}, {Domain}); authorization filed.",
            campaign.Id, entry.Id, targetStage, domain);
        return campaign;
    }

    public async Task HandleAuthorizationDecisionAsync(HumanProposal proposal, bool approve, string decidedBy, CancellationToken ct = default)
    {
        var campaign = await _campaigns.GetAsync(proposal.SubjectId, ct);
        if (campaign is null) { _logger.LogWarning("Authorization for unknown campaign {Id}.", proposal.SubjectId); return; }

        if (!approve)
        {
            await _campaigns.UpdateAsync(campaign with { Status = CampaignStatus.Rejected }, ct);
            _logger.LogInformation("Campaign {Id} authorization REJECTED by {By}.", campaign.Id, decidedBy);
            return;
        }

        var entry = await _innovated.GetAsync(campaign.EntryId, ct);
        if (entry is null) { _logger.LogWarning("Campaign {Id} entry {Entry} missing.", campaign.Id, campaign.EntryId); return; }

        // 1) Human-authored authorization event → UnderTest (a campaign is now active). Rank rises, so this
        //    is only legal because the kind is human-authored (invariant §0a, enforced by the store guard).
        if (ProvenancePolicy.TrustRank(entry.Provenance) < ProvenancePolicy.TrustRank(Provenance.UnderTest))
        {
            await _innovated.UpdateAsync(
                entry with { Provenance = Provenance.UnderTest },
                new LedgerEvent(LedgerEventKind.HumanAuthorizeCampaign,
                    $"campaign {campaign.Id} authorized by {decidedBy}", CampaignId: campaign.Id), ct);
        }

        var approvedBudget = campaign.Protocol.Sum(s => Math.Max(1, s.Budget));
        campaign = campaign with
        {
            Status = CampaignStatus.Running,
            Authorization = new CampaignAuthorization(decidedBy, approvedBudget, DateTime.UtcNow, campaign.PromotionPreauthorized),
        };
        await _campaigns.UpdateAsync(campaign, ct);

        // 2) Run each pre-registered step as a child packet. A missing environment parks the campaign.
        var blocked = await RunStepsAsync(campaign, entry, ct);
        if (blocked)
        {
            await _campaigns.UpdateAsync(campaign with { Status = CampaignStatus.Blocked }, ct);
            return;
        }

        // 3) Mechanical verdict over the pre-registered criteria × collected evidence.
        var verdict = await _campaigns.ComputeVerdictAsync(campaign.Id, ct);
        _logger.LogInformation("Campaign {Id} verdict: {Verdict}.", campaign.Id, verdict);

        if (verdict == CampaignVerdict.Passed)
        {
            if (campaign.PromotionPreauthorized)   // general + pre-authorized → the 2nd touch was given at design time
            {
                await PromoteAsync(campaign, decidedBy, "auto (pre-authorized at design time)", ct);
                await _campaigns.UpdateAsync(campaign with { Status = CampaignStatus.Completed }, ct);
            }
            else
            {
                await FilePromotionProposalAsync(campaign, ct);   // 2nd human touch (mandatory for sensitive)
                await _campaigns.UpdateAsync(campaign with { Status = CampaignStatus.AwaitingPromotion }, ct);
            }
        }
        else if (verdict == CampaignVerdict.Failed)
        {
            await DemoteOnFailureAsync(campaign, ct);
            await _campaigns.UpdateAsync(campaign with { Status = CampaignStatus.Rejected }, ct);
        }
        else
        {
            // Pending/Inconclusive with no hard block — leave it blocked for follow-up rather than concluding.
            await _campaigns.UpdateAsync(campaign with { Status = CampaignStatus.Blocked }, ct);
        }
    }

    public async Task HandlePromotionDecisionAsync(HumanProposal proposal, bool approve, string decidedBy, CancellationToken ct = default)
    {
        var campaign = await _campaigns.GetAsync(proposal.SubjectId, ct);
        if (campaign is null) { _logger.LogWarning("Promotion for unknown campaign {Id}.", proposal.SubjectId); return; }

        if (!approve)
        {
            await _campaigns.UpdateAsync(campaign with { Status = CampaignStatus.Rejected }, ct);
            _logger.LogInformation("Campaign {Id} promotion REJECTED by {By}; entry stays at its current stage.", campaign.Id, decidedBy);
            return;
        }

        await PromoteAsync(campaign, decidedBy, "confirmed (2nd human touch)", ct);
        await _campaigns.UpdateAsync(campaign with { Status = CampaignStatus.Completed }, ct);
    }

    // ── steps ──

    /// <summary>Runs each step as a child packet. Returns true if the campaign is BLOCKED (a step's
    /// environment does not exist) so the caller parks it.</summary>
    private async Task<bool> RunStepsAsync(ValidationCampaign campaign, InnovatedKnowledgeRecord entry, CancellationToken ct)
    {
        foreach (var step in campaign.Protocol)
        {
            if (!EnvironmentExists(step))
            {
                await RecordBlockedStepAsync(campaign, entry, step, ct);
                return true;   // legally parked pending an environment (tooling proposal lands in sub-unit 4)
            }

            var child = NodePacket.Create(
                intent: $"Validation step '{step.Id}' for campaign {campaign.Id}: {step.Description}",
                address: step.Environment,
                capability: step.Capability,
                correlationId: campaign.CorrelationId,
                slots: new Dictionary<string, string>
                {
                    [PacketSlots.CampaignId] = campaign.Id,
                    [PacketSlots.CampaignStepId] = step.Id,
                    [PacketSlots.Question] = campaign.HypothesisSnapshot,
                });

            var result = await _router.DispatchAsync(child, ct);

            var (outcome, measurements) = ReadStepResult(result, step);
            await _campaigns.RecordStepEvidenceAsync(campaign.Id,
                new StepEvidence(step.Id, outcome, measurements, result.LastEntry?.Decision, result.Id), ct);
        }
        return false;
    }

    private bool EnvironmentExists(ValidationStep step)
        => _nodes.Any(n => n.Id == step.Environment || n.Capabilities.Contains(step.Capability));

    private async Task RecordBlockedStepAsync(ValidationCampaign campaign, InnovatedKnowledgeRecord entry, ValidationStep step, CancellationToken ct)
    {
        await _campaigns.RecordStepEvidenceAsync(campaign.Id,
            new StepEvidence(step.Id, ValidationStepOutcome.Blocked, new Dictionary<string, double>(),
                $"no environment for capability {step.Capability} / node {step.Environment}"), ct);

        // The honest "needs external input" (§6) — a gap the human/goal system surfaces.
        await _gaps.AddAsync(new GapRecord
        {
            CorrelationId = campaign.CorrelationId,
            OriginNode = NodeId.Innovation,
            Question = $"Validation step '{step.Id}' ({step.Kind}) has no environment to run in.",
            Intent = entry.Intent,
            Missing = $"a node providing capability {step.Capability} (environment {step.Environment})",
            Status = GapStatus.Open,
        }, ct);

        _logger.LogInformation("Campaign {Id} blocked: step {Step} needs a missing environment ({Cap}/{Env}).",
            campaign.Id, step.Id, step.Capability, step.Environment);
    }

    private static (ValidationStepOutcome, IReadOnlyDictionary<string, double>) ReadStepResult(NodePacket result, ValidationStep step)
    {
        var measurements = ParseMeasurements(result.Payload.Slot(PacketSlots.StepMeasurements));

        // An explicit outcome slot wins; else derive from the packet's terminal success flag.
        var explicitOutcome = result.Payload.Slot(PacketSlots.StepOutcome);
        if (!string.IsNullOrWhiteSpace(explicitOutcome) &&
            Enum.TryParse<ValidationStepOutcome>(explicitOutcome, ignoreCase: true, out var parsed))
            return (parsed, measurements);

        var succeeded = result.State == NodeState.Succeeded && (result.LastEntry?.Success ?? false);
        return (succeeded ? ValidationStepOutcome.Passed : ValidationStepOutcome.Failed, measurements);
    }

    // ── trust changes (all human-authored except the failure demotion) ──

    private async Task PromoteAsync(ValidationCampaign campaign, string who, string reason, CancellationToken ct)
    {
        var entry = await _innovated.GetAsync(campaign.EntryId, ct);
        if (entry is null) return;

        var cap = ProvenancePolicy.CapFor(campaign.TargetStage, campaign.Domain);
        double score;
        if (cap is double c) score = c;   // mid-tier: promote to the domain-capped ceiling
        else
        {
            var (successes, _) = await _innovated.CountDistinctOutcomesAsync(entry.Id, ct);
            score = Math.Clamp(0.6 + 0.05 * successes, 0.6, 0.9);   // trusted tier (uncapped)
        }

        await _innovated.UpdateAsync(
            entry with
            {
                Provenance = campaign.TargetStage,
                Confidence = ProvenancePolicy.Clamp(campaign.TargetStage, Confidence.Of(score), campaign.Domain),
            },
            new LedgerEvent(LedgerEventKind.HumanConfirmPromotion,
                $"promoted to {campaign.TargetStage} via campaign {campaign.Id} — {reason} by {who}", CampaignId: campaign.Id), ct);

        _logger.LogInformation("Entry {Entry} promoted to {Stage} (conf {Score:0.##}) via campaign {Id}.",
            entry.Id, campaign.TargetStage, score, campaign.Id);
    }

    private async Task DemoteOnFailureAsync(ValidationCampaign campaign, CancellationToken ct)
    {
        var entry = await _innovated.GetAsync(campaign.EntryId, ct);
        if (entry is null) return;
        var demoted = ProvenancePolicy.DemoteOneStage(entry.Provenance);
        await _innovated.UpdateAsync(
            entry with { Provenance = demoted },
            new LedgerEvent(LedgerEventKind.FailureEvidence,
                $"campaign {campaign.Id} failed its pre-registered criteria", CampaignId: campaign.Id), ct);
    }

    private async Task FilePromotionProposalAsync(ValidationCampaign campaign, CancellationToken ct)
    {
        var evidence = await _campaigns.GetStepEvidenceAsync(campaign.Id, ct);
        var proposal = new HumanProposal
        {
            CorrelationId = campaign.CorrelationId,
            Kind = HumanProposalKind.PromoteFromCampaign,
            SubjectId = campaign.Id,
            TargetProvenance = campaign.TargetStage,
            Title = $"Confirm promotion after passed campaign: {Truncate(campaign.HypothesisSnapshot, 60)}",
            Summary = $"Campaign {campaign.Id} PASSED its pre-registered criteria; confirm promotion to {campaign.TargetStage}.",
            JustificationJson = JsonSerializer.Serialize(new { campaign.Id, campaign.TargetStage, verdict = "Passed", evidence }, JsonOpts),
            // No parked packet: the entry is usable at UnderTest now; the 2nd touch is asynchronous.
        };
        await _proposals.AddAsync(proposal, ct);
    }

    private static IReadOnlyDictionary<string, double> ParseMeasurements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, double>();
        try { return JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new(); }
        catch { return new Dictionary<string, double>(); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
