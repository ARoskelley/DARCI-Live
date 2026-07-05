#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

/// <summary>
/// Sub-unit 2 lifecycle: draft → authorize (HumanAuthorizeCampaign → UnderTest) → steps as child packets →
/// mechanical verdict → promotion touch. Sensitive never auto-promotes; a missing environment parks the
/// campaign on a gap. Driven through the real HumanGate to prove the delegation wiring.
/// </summary>
public sealed class CampaignCoordinatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _conn;
    private readonly SqliteValidationCampaignStore _campaigns;
    private readonly SqliteInnovatedKnowledgeStore _innovated;
    private readonly SqliteProposalStore _proposals;
    private readonly SqliteGapStore _gaps;
    private readonly SqliteNodePacketStore _packets;

    public CampaignCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-coord-{Guid.NewGuid():N}.db");
        _conn = $"Data Source={_dbPath}";
        _campaigns = new SqliteValidationCampaignStore(_conn, NullLogger<SqliteValidationCampaignStore>.Instance);
        _innovated = new SqliteInnovatedKnowledgeStore(_conn, NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        _proposals = new SqliteProposalStore(_conn, NullLogger<SqliteProposalStore>.Instance);
        _gaps = new SqliteGapStore(_conn, NullLogger<SqliteGapStore>.Instance);
        _packets = new SqliteNodePacketStore(_conn, NullLogger<SqliteNodePacketStore>.Instance);
        _campaigns.InitializeAsync().GetAwaiter().GetResult();
        _innovated.InitializeAsync().GetAwaiter().GetResult();
        _proposals.InitializeAsync().GetAwaiter().GetResult();
        _gaps.InitializeAsync().GetAwaiter().GetResult();
        _packets.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // ── fakes ──

    private sealed class FakeNode : INode
    {
        public NodeId Id { get; }
        public IReadOnlySet<Capability> Capabilities { get; }
        public FakeNode(NodeId id, params Capability[] caps) { Id = id; Capabilities = new HashSet<Capability>(caps); }
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default) => Task.FromResult(packet);
    }

    private sealed class FakeRouter : INodeRouter
    {
        private readonly Func<NodePacket, NodePacket> _respond;
        public int DispatchCount;
        public FakeRouter(Func<NodePacket, NodePacket> respond) => _respond = respond;
        public Task<NodePacket> DispatchAsync(NodePacket packet, CancellationToken ct = default)
        {
            DispatchCount++;
            return Task.FromResult(_respond(packet));
        }
    }

    private sealed class FakeProtocolCritic : IProtocolCritic
    {
        private readonly ProtocolCritique _c;
        public int CallCount;
        public FakeProtocolCritic(ProtocolCritique? c = null) => _c = c ?? new ProtocolCritique(System.Array.Empty<string>(), true, "ok");
        public Task<ProtocolCritique> FalsifyAsync(ValidationCampaign campaign, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_c);
        }
    }

    // ── helpers ──

    private CampaignCoordinator Coordinator(INodeRouter router, IEnumerable<INode> nodes, IProtocolCritic? critic = null) =>
        new(_campaigns, _innovated, _proposals, router, _packets, _gaps, critic ?? new FakeProtocolCritic(), nodes,
            NullLogger<CampaignCoordinator>.Instance);

    private HumanGateService Gate(ICampaignCoordinator coordinator) =>
        new(_proposals, _innovated, _packets, NullLogger<HumanGateService>.Instance, coordinator);

    private async Task<InnovatedKnowledgeRecord> SeedEntryAsync()
    {
        var rec = new InnovatedKnowledgeRecord
        {
            CorrelationId = "corr-1", Hypothesis = "combine A and B", Topic = "t", Intent = "i",
            Provenance = Provenance.Innovated, Confidence = Confidence.Of(0.3),
        };
        await _innovated.AddAsync(rec);
        return rec;
    }

    private async Task<NodePacket> WorkingParentAsync()
    {
        var p = NodePacket.Create("innovate X", capability: Capability.Innovate, correlationId: "corr-1")
            .Transition(NodeId.Innovation, NodeState.Routed, "r")
            .Transition(NodeId.Innovation, NodeState.Accepted, "a")
            .Transition(NodeId.Innovation, NodeState.Working, "w", leaseFor: TimeSpan.FromMinutes(5));
        await _packets.CreatePacketAsync(p);
        return p;
    }

    private static ValidationStep SandboxStep() =>
        new("s1", ValidationStepKind.SandboxTest, Capability.RunTests, NodeId.Coding,
            new SuccessCriteria("pass_rate", Comparator.GreaterOrEqual, 0.9), "sandbox build+test");

    private static ValidationStep ResearchStep() =>
        new("s2", ValidationStepKind.ExternalResearchCheck, Capability.AnswerKnowledge, NodeId.Knowledge,
            new SuccessCriteria("corroborations", Comparator.GreaterOrEqual, 2), "literature corroboration");

    private static IReadOnlyList<ValidationStep> TwoStep() => new[] { SandboxStep(), ResearchStep() };

    private static INode[] BothEnvironments() =>
        new INode[] { new FakeNode(NodeId.Coding, Capability.RunTests), new FakeNode(NodeId.Knowledge, Capability.AnswerKnowledge) };

    /// <summary>Router that drives a child step packet to Succeeded, writing the given measurements.</summary>
    private static FakeRouter PassingRouter(double passRate = 0.95, double corroborations = 3) => new(child =>
    {
        var stepId = child.Payload.Slot(PacketSlots.CampaignStepId);
        var meas = stepId == "s1"
            ? new Dictionary<string, double> { ["pass_rate"] = passRate }
            : new Dictionary<string, double> { ["corroborations"] = corroborations };
        return child
            .Transition(NodeId.Coding, NodeState.Routed, "r")
            .Transition(NodeId.Coding, NodeState.Accepted, "a")
            .Transition(NodeId.Coding, NodeState.Working, "w", leaseFor: TimeSpan.FromMinutes(1))
            .WithSlot(PacketSlots.StepMeasurements, JsonSerializer.Serialize(meas))
            .Transition(NodeId.Coding, NodeState.Succeeded, "done", success: true);
    });

    private async Task<HumanProposal> PendingOfKindAsync(HumanProposalKind kind)
        => (await _proposals.GetPendingAsync()).Single(p => p.Kind == kind);

    // ── tests ──

    [Fact]
    public async Task Draft_FilesAuthorization_ParksParent_CreatesCampaign()
    {
        var entry = await SeedEntryAsync();
        var parent = await WorkingParentAsync();
        var critic = new FakeProtocolCritic();

        var campaign = await Coordinator(PassingRouter(), BothEnvironments(), critic)
            .DraftAndRequestAuthorizationAsync(entry, TwoStep(), Provenance.ProvisionallyValidated, KnowledgeDomain.Sensitive, parent);

        Assert.Equal(1, critic.CallCount);   // protocol was falsified before the human sees it

        var stored = await _campaigns.GetAsync(campaign.Id);
        Assert.Equal(CampaignStatus.AwaitingAuthorization, stored!.Status);

        var proposal = await PendingOfKindAsync(HumanProposalKind.AuthorizeCampaign);
        Assert.Equal(campaign.Id, proposal.SubjectId);
        Assert.Equal(parent.Id, proposal.ParkedPacketId);

        var parked = await _packets.GetPacketAsync(parent.Id);
        Assert.Equal(NodeState.AwaitingDependency, parked!.State);
        Assert.Null(parked.LeaseExpiresAt);   // lease cleared while parked
    }

    [Theory]
    [InlineData(KnowledgeDomain.Sensitive, false)]
    [InlineData(KnowledgeDomain.General, true)]
    public async Task Draft_PreauthorizeOnlyForGeneral(KnowledgeDomain domain, bool expectedPreauth)
    {
        var entry = await SeedEntryAsync();
        var parent = await WorkingParentAsync();

        var campaign = await Coordinator(PassingRouter(), BothEnvironments())
            .DraftAndRequestAuthorizationAsync(entry, TwoStep(), Provenance.ProvisionallyValidated, domain, parent, preauthorizePromotion: true);

        Assert.Equal(expectedPreauth, (await _campaigns.GetAsync(campaign.Id))!.PromotionPreauthorized);
    }

    [Fact]
    public async Task Authorize_PassedSensitive_MovesToUnderTest_RunsSteps_FilesPromotionTouch_NoAutoPromote()
    {
        var entry = await SeedEntryAsync();
        var parent = await WorkingParentAsync();
        var router = PassingRouter();
        var coordinator = Coordinator(router, BothEnvironments());

        await coordinator.DraftAndRequestAuthorizationAsync(entry, TwoStep(), Provenance.ProvisionallyValidated, KnowledgeDomain.Sensitive, parent);
        var auth = await PendingOfKindAsync(HumanProposalKind.AuthorizeCampaign);
        await Gate(coordinator).DecideAsync(auth.Id, approve: true, note: null, decidedBy: "tinman");

        // Human authorization moved the entry to UnderTest via a human-authored ledger event — still capped.
        var after = await _innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.UnderTest, after!.Provenance);
        Assert.True(after.Confidence.IsLow);
        var revs = await _innovated.GetRevisionsAsync(entry.Id);
        Assert.Contains(revs, r => r.Kind == LedgerEventKind.HumanAuthorizeCampaign);

        // Both pre-registered steps ran as child packets and their evidence produced a Passed verdict.
        Assert.Equal(2, router.DispatchCount);
        Assert.Equal(CampaignVerdict.Passed, await _campaigns.ComputeVerdictAsync((await _campaigns.GetByEntryAsync(entry.Id))[0].Id));

        // Sensitive → the 2nd human touch is filed, NOT auto-applied. Entry is NOT yet promoted.
        Assert.Single(await _proposals.GetPendingAsync());   // the PromoteFromCampaign proposal
        var promo = await PendingOfKindAsync(HumanProposalKind.PromoteFromCampaign);
        Assert.Equal(Provenance.ProvisionallyValidated, promo.TargetProvenance);
        Assert.DoesNotContain(revs, r => r.Kind == LedgerEventKind.HumanConfirmPromotion);
    }

    [Fact]
    public async Task Authorize_PassedGeneralPreauthorized_AutoPromotes_ToMidTierCap()
    {
        var entry = await SeedEntryAsync();
        var parent = await WorkingParentAsync();
        var coordinator = Coordinator(PassingRouter(), BothEnvironments());

        await coordinator.DraftAndRequestAuthorizationAsync(entry, TwoStep(), Provenance.ProvisionallyValidated, KnowledgeDomain.General, parent, preauthorizePromotion: true);
        var auth = await PendingOfKindAsync(HumanProposalKind.AuthorizeCampaign);
        await Gate(coordinator).DecideAsync(auth.Id, approve: true, note: null, decidedBy: "tinman");

        var after = await _innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.ProvisionallyValidated, after!.Provenance);
        Assert.Equal(ProvenancePolicy.ProvisionalCapGeneral, after.Confidence.Score, 5);   // 0.6 mid-tier
        Assert.False(after.Confidence.IsLow);

        var campaign = (await _campaigns.GetByEntryAsync(entry.Id))[0];
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
        Assert.Empty(await _proposals.GetPendingAsync());   // no 2nd proposal — it was pre-authorized
    }

    [Fact]
    public async Task Authorize_FailedPreRegisteredCriteria_DemotesAndRejects()
    {
        var entry = await SeedEntryAsync();
        var parent = await WorkingParentAsync();
        // The node reports "success" but the sandbox pass_rate (0.5) misses the pre-registered 0.9 bar.
        var coordinator = Coordinator(PassingRouter(passRate: 0.5), BothEnvironments());

        await coordinator.DraftAndRequestAuthorizationAsync(entry, TwoStep(), Provenance.ProvisionallyValidated, KnowledgeDomain.General, parent, preauthorizePromotion: true);
        var auth = await PendingOfKindAsync(HumanProposalKind.AuthorizeCampaign);
        await Gate(coordinator).DecideAsync(auth.Id, approve: true, note: null, decidedBy: "tinman");

        var after = await _innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.Innovated, after!.Provenance);   // UnderTest → demoted one stage on failure
        var campaign = (await _campaigns.GetByEntryAsync(entry.Id))[0];
        Assert.Equal(CampaignStatus.Rejected, campaign.Status);
        Assert.Empty(await _proposals.GetPendingAsync());
    }

    [Fact]
    public async Task Authorize_MissingEnvironment_BlocksCampaign_FilesGap()
    {
        var entry = await SeedEntryAsync();
        var parent = await WorkingParentAsync();
        // Protocol needs a simulation environment (Cad) that no node provides.
        var protocol = new[]
        {
            new ValidationStep("sim", ValidationStepKind.Simulation, Capability.GenerateCad, NodeId.Cad,
                new SuccessCriteria("stable", Comparator.GreaterOrEqual, 1), "physics sim"),
        };
        var coordinator = Coordinator(PassingRouter(), BothEnvironments());   // no Cad node present

        await coordinator.DraftAndRequestAuthorizationAsync(entry, protocol, Provenance.ProvisionallyValidated, KnowledgeDomain.General, parent, preauthorizePromotion: true);
        var auth = await PendingOfKindAsync(HumanProposalKind.AuthorizeCampaign);
        await Gate(coordinator).DecideAsync(auth.Id, approve: true, note: null, decidedBy: "tinman");

        var campaign = (await _campaigns.GetByEntryAsync(entry.Id))[0];
        Assert.Equal(CampaignStatus.Blocked, campaign.Status);

        var evidence = await _campaigns.GetStepEvidenceAsync(campaign.Id);
        Assert.Equal(ValidationStepOutcome.Blocked, evidence.Single().Outcome);

        var openGaps = await _gaps.GetByStatusAsync(GapStatus.Open);
        Assert.Contains(openGaps, g => g.Missing.Contains("GenerateCad"));

        // No promotion happened; the entry sits at UnderTest awaiting the missing environment.
        Assert.Equal(Provenance.UnderTest, (await _innovated.GetAsync(entry.Id))!.Provenance);
    }

    [Fact]
    public async Task PromotionTouch_Approved_PromotesToDomainCap()
    {
        var entry = await SeedEntryAsync();
        var parent = await WorkingParentAsync();
        var coordinator = Coordinator(PassingRouter(), BothEnvironments());

        await coordinator.DraftAndRequestAuthorizationAsync(entry, TwoStep(), Provenance.ProvisionallyValidated, KnowledgeDomain.Sensitive, parent);
        await Gate(coordinator).DecideAsync((await PendingOfKindAsync(HumanProposalKind.AuthorizeCampaign)).Id, true, null, "tinman");

        // Now approve the 2nd touch.
        var promo = await PendingOfKindAsync(HumanProposalKind.PromoteFromCampaign);
        await Gate(coordinator).DecideAsync(promo.Id, approve: true, note: "confirmed", decidedBy: "tinman");

        var after = await _innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.ProvisionallyValidated, after!.Provenance);
        Assert.Equal(ProvenancePolicy.ProvisionalCapSensitive, after.Confidence.Score, 5);   // 0.45 sensitive cap
        var campaign = (await _campaigns.GetByEntryAsync(entry.Id))[0];
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
    }

    [Fact]
    public async Task Authorize_Rejected_ClosesCampaign_EntryUnchanged()
    {
        var entry = await SeedEntryAsync();
        var parent = await WorkingParentAsync();
        var router = PassingRouter();
        var coordinator = Coordinator(router, BothEnvironments());

        await coordinator.DraftAndRequestAuthorizationAsync(entry, TwoStep(), Provenance.ProvisionallyValidated, KnowledgeDomain.Sensitive, parent);
        var auth = await PendingOfKindAsync(HumanProposalKind.AuthorizeCampaign);
        await Gate(coordinator).DecideAsync(auth.Id, approve: false, note: "design too weak", decidedBy: "tinman");

        Assert.Equal(0, router.DispatchCount);   // no steps ran
        Assert.Equal(Provenance.Innovated, (await _innovated.GetAsync(entry.Id))!.Provenance);
        Assert.Equal(CampaignStatus.Rejected, (await _campaigns.GetByEntryAsync(entry.Id))[0].Status);
    }
}
