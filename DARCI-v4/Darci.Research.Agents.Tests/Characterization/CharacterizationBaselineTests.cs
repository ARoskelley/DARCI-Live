#nullable enable

using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;

namespace Darci.Research.Agents.Tests.Characterization;

/// <summary>
/// SU0 — THE GOLDEN ORACLE for the Phase 1 core carve.
///
/// These tests pin CURRENT cross-subsystem behavior before the router/dispatch carve begins. They are not
/// unit tests of any one type; they assert the emergent wiring: router → coding → knowledge → gap handler →
/// innovation → serve point (consumption link) → outcome sink → ledger, and the human gate / campaign path.
///
/// PROTOCOL: re-run unchanged after every risky sub-unit (SU3 especially). Any failure is behavior change —
/// explain it or revert it. Do NOT "fix" a test here to make a refactor pass without a deliberate re-bless.
///
/// The chain shapes below were cross-checked against the real 2026-07-08 end-to-end run (Run A/B), so this
/// harness is known to reproduce observed production behavior, not just its own assumptions.
/// </summary>
public class CharacterizationBaselineTests
{
    private static InnovationProposal SolvableHypothesis() => new()
    {
        Status = ProposalStatus.VettedInternally,
        Hypothesis = "derive the TB-7 checksum from the frame-length prefix and a byte-sum",
        Reasoning = new[] { new ReasoningLink("length-prefixed frames commonly checksum the payload", new[] { "f1" }) },
        Provenance = Provenance.Innovated,
        Plausibility = new KnowledgeReview(true, Confidence.Of(0.7), Array.Empty<string>(), "plausible"),
        Confidence = ProvenancePolicy.Clamp(Provenance.Innovated, Confidence.Of(0.3)),
    };

    // ─────────────────────────── Flow A: honest Unsolvable (the observed Run B shape) ───────────────────────────

    [Fact]
    public async Task FlowA_EscalationToUnsolvable_PersistsNothing()
    {
        using var h = new CharacterizationHarness();   // default innovation result = CannotSolve

        var result = await h.RunCodingTaskAsync();
        var snap = await h.SnapshotAsync(result.CorrelationId);

        // Three packets, all under ONE correlation root, all terminal-Succeeded, no lease left held.
        Assert.Equal(3, snap.Chains.Count);
        Assert.All(snap.Chains, c => Assert.Equal("Succeeded", c.Terminal));
        Assert.All(snap.Chains, c => Assert.False(c.LeaseHeld));

        Assert.Equal("Routed>Accepted>Working>Working>Succeeded", Chain(snap, "WriteCode").States);
        Assert.Equal("Routed>Accepted>Working>Working>Working>Succeeded", Chain(snap, "FillKnowledgeGap").States);
        Assert.Equal("Routed>Accepted>Working>Succeeded", Chain(snap, "Innovate").States);

        // Honest Unsolvable ⇒ NOTHING persisted. (Phantom evidence here would be a real bug.)
        Assert.Empty(snap.Innovated);
        Assert.Empty(snap.Consumption);
        Assert.Empty(snap.RevisionKinds);
        Assert.Empty(snap.Proposals);

        // Gaps deferred for learning: the original gap + the required external input.
        Assert.Equal(new[] { "deferred=2" }, snap.GapStatuses);

        // Outcome emitted exactly once, under the correlation root.
        Assert.Equal(1, snap.OutcomeEmissions);
        Assert.Equal(1, snap.OutcomeEmissionsUnderRoot);
    }

    // ─────────────────────── Flow B: the evidence loop end-to-end (the one that matters) ───────────────────────

    [Fact]
    public async Task FlowB_SolvableHypothesis_ServesLink_AndOutcomeResolvesIt()
    {
        using var h = new CharacterizationHarness(innovationResult: SolvableHypothesis());

        var result = await h.RunCodingTaskAsync();
        var snap = await h.SnapshotAsync(result.CorrelationId);

        Assert.Equal(3, snap.Chains.Count);

        // Persisted as a capped Innovated hypothesis; the success evidence nudged the WITHIN-CAP ranking
        // score to 0.2 and it is STILL IsLow. Provenance never rose (no human event).
        var entry = Assert.Single(snap.Innovated);
        Assert.Equal("Innovated", entry.Provenance);
        Assert.Equal(0.2, entry.Score, 4);
        Assert.True(entry.IsLow);
        Assert.True(entry.Score <= ProvenancePolicy.InnovatedCap);
        Assert.Equal(1, entry.Successes);
        Assert.Equal(0, entry.Failures);

        // *** The serve point: a consumption link keyed to the CORRELATION ROOT, resolved by the outcome. ***
        var link = Assert.Single(snap.Consumption);
        Assert.True(link.RootIsCorrelation, "consumption link must be keyed to the correlation root");
        Assert.Equal("Success", link.Outcome);
        Assert.True(link.Resolved);

        // Ledger: created, then success evidence appended. No promotion.
        Assert.Equal(new[] { "Created", "SuccessEvidence" }, snap.RevisionKinds);

        // A promotion proposal was filed for the human, un-parked (the capped hypothesis is usable now).
        Assert.Equal(new[] { "PromoteInnovated:Pending:parked=False" }, snap.Proposals);

        Assert.Equal(new[] { "deferred=1" }, snap.GapStatuses);
        Assert.Equal(1, snap.OutcomeEmissions);
        Assert.Equal(1, snap.OutcomeEmissionsUnderRoot);
    }

    // ─────────── ADD-2 GUARD: goal_id/correlation-root identity is what the loop hangs on ───────────

    [Fact]
    public async Task Add2Guard_OutcomeResolvesOnlyUnderTheCorrelationRoot_NeverAForeignId()
    {
        using var h = new CharacterizationHarness(innovationResult: SolvableHypothesis());

        var result = await h.RunCodingTaskAsync();
        var root = result.CorrelationId;

        // All three strings that must be the SAME value for the evidence loop to work at all:
        //   the packet's correlation root, the recorded consumption root, and the emitted outcome root.
        var entry = Assert.Single(await h.Innovated.GetByCorrelationAsync(root));
        var consumption = Assert.Single(await h.Innovated.GetConsumptionsAsync(entry.Id));
        Assert.Equal(root, consumption.CorrelationRoot);
        Assert.Equal(root, Assert.Single(h.EmittedOutcomeRoots));

        var before = await h.Innovated.CountDistinctOutcomesAsync(entry.Id);
        Assert.Equal((1, 0), before);

        // A FOREIGN id — e.g. a fresh per-invocation trace_id — must resolve NOTHING. If a future adapter
        // ever keys correlation off trace_id instead of goal_id, this is the test that catches it before the
        // whole evidence loop goes silently inert.
        await h.Sink.ApplyAsync(new OutcomeFeedback($"trace-{Guid.NewGuid():N}", Success: true));
        Assert.Equal(before, await h.Innovated.CountDistinctOutcomesAsync(entry.Id));

        // And a retry under the SAME root collapses (distinct-root counting), rather than inflating.
        await h.Sink.ApplyAsync(new OutcomeFeedback(root, Success: true));
        Assert.Equal(before, await h.Innovated.CountDistinctOutcomesAsync(entry.Id));
    }

    // ─────────────────────────── Flow C: the human gate is the only way above the cap ───────────────────────────

    [Fact]
    public async Task FlowC_HumanApproval_IsTheOnlyPathAboveTheCap()
    {
        using var h = new CharacterizationHarness(innovationResult: SolvableHypothesis());
        var result = await h.RunCodingTaskAsync();
        var root = result.CorrelationId;

        var proposal = Assert.Single(await h.Proposals.GetPendingAsync());
        Assert.Equal(HumanProposalKind.PromoteInnovated, proposal.Kind);

        var decision = await h.Gate().DecideAsync(proposal.Id, approve: true, note: "sound", decidedBy: "tinman");
        Assert.True(decision.Applied);

        var snap = await h.SnapshotAsync(root);
        var entry = Assert.Single(snap.Innovated);
        Assert.Equal("HumanApproved", entry.Provenance);
        Assert.True(entry.Score > ProvenancePolicy.InnovatedCap);   // above the cap — human-authored only
        Assert.False(entry.IsLow);
        Assert.Equal(new[] { "Created", "SuccessEvidence", "HumanConfirmPromotion" }, snap.RevisionKinds);
    }

    // ─────────────── Flow D: campaign authorization + steps, THROUGH THE REAL ROUTER ───────────────

    [Fact]
    public async Task FlowD_CampaignParksThenRunsStepsThroughTheRealRouter()
    {
        using var h = new CharacterizationHarness(codingEscalates: false);

        var entry = new InnovatedKnowledgeRecord
        {
            CorrelationId = "corr-campaign", Hypothesis = "a validated hypothesis", Topic = "t", Intent = "i",
            Provenance = Provenance.Innovated, Confidence = Confidence.Of(0.3),
        };
        await h.Innovated.AddAsync(entry);

        var parent = NodePacket.Create("validate it", capability: Capability.Innovate, correlationId: "corr-campaign")
            .Transition(NodeId.Innovation, NodeState.Routed, "r")
            .Transition(NodeId.Innovation, NodeState.Accepted, "a")
            .Transition(NodeId.Innovation, NodeState.Working, "w", leaseFor: TimeSpan.FromMinutes(5));
        await h.Packets.CreatePacketAsync(parent);

        var coordinator = h.Coordinator();
        var protocol = new[]
        {
            new ValidationStep("sandbox", ValidationStepKind.SandboxTest, Capability.RunTests, NodeId.Coding,
                new SuccessCriteria("pass_rate", Comparator.GreaterOrEqual, 0.9), "sandbox build+test"),
        };

        await coordinator.DraftAndRequestAuthorizationAsync(entry, protocol,
            Provenance.ProvisionallyValidated, KnowledgeDomain.Sensitive, parent);

        // PARKED: AwaitingDependency with the lease CLEARED — the core-side long-lived wait.
        var parked = await h.Packets.GetPacketAsync(parent.Id);
        Assert.Equal(NodeState.AwaitingDependency, parked!.State);
        Assert.Null(parked.LeaseExpiresAt);

        var auth = Assert.Single(await h.Proposals.GetPendingAsync());
        Assert.Equal(HumanProposalKind.AuthorizeCampaign, auth.Kind);
        Assert.Equal(parent.Id, auth.ParkedPacketId);

        // Authorize → the step runs as a CHILD PACKET dispatched through the REAL router to the coding node.
        await h.Gate(coordinator).DecideAsync(auth.Id, approve: true, note: null, decidedBy: "tinman");

        var campaign = Assert.Single(await h.Campaigns.GetByEntryAsync(entry.Id));
        Assert.Equal(CampaignVerdict.Passed, await h.Campaigns.ComputeVerdictAsync(campaign.Id));
        Assert.Equal(CampaignStatus.AwaitingPromotion, campaign.Status);

        // Human authorization moved it to UnderTest (human-authored) but SENSITIVE never auto-promotes:
        // the second touch is filed, not applied.
        var after = await h.Innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.UnderTest, after!.Provenance);
        Assert.True(after.Confidence.IsLow);
        Assert.Equal(HumanProposalKind.PromoteFromCampaign,
            Assert.Single(await h.Proposals.GetPendingAsync()).Kind);

        var revisions = await h.Innovated.GetRevisionsAsync(entry.Id);
        Assert.Contains(revisions, r => r.Kind == LedgerEventKind.HumanAuthorizeCampaign);
        Assert.DoesNotContain(revisions, r => r.Kind == LedgerEventKind.HumanConfirmPromotion);
    }

    // ─────────────────── Router contract: capability resolution is the single seam ───────────────────

    [Fact]
    public async Task Router_UnresolvableCapability_BlocksPacketWithNamedError()
    {
        // *** RE-BLESSED IN PHASE 3 SU 3.1, NOT A REGRESSION ***
        // This asserted NodeState.Failed and Success == false. Phase 3 Fork 1 (approved) makes "no node
        // serves this capability" a BLOCKED outcome, not a failure: nothing was attempted, so reporting
        // failure both lies to a core running without that node and feeds phantom negative evidence into
        // the confidence and campaign paths. NodeState.Blocked is a third TERMINAL state, so the packet
        // still terminates and is still named — only the verdict changed, from "it broke" to "nothing
        // here can do this". Success is now null (neither true nor false) for the same reason.
        using var h = new CharacterizationHarness();

        // No node advertises DesignGeometry.
        var packet = NodePacket.Create("design a bracket", capability: Capability.DesignGeometry);
        var result = await h.Router.DispatchAsync(packet);

        Assert.Equal(NodeState.Blocked, result.State);
        Assert.True(result.State.IsTerminal());
        Assert.False(result.State.IsFailure());
        Assert.Null(result.LastEntry!.Success);
        Assert.Contains("No node", result.LastEntry.Decision);
        Assert.NotNull(result.LastEntry.Error);
    }

    private static PacketChain Chain(BehaviorSnapshot snap, string capability) =>
        snap.Chains.Single(c => c.Capability == capability);
}
