#nullable enable

using Darci.Nodes;
using Darci.Research.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

/// <summary>
/// Sub-task: the auto-draft eligibility sweep. It auto-DRAFTS campaigns (low priority) for eligible entries,
/// each still parked for human authorization — it never authorizes, runs, or promotes anything.
/// </summary>
public sealed class CampaignEligibilitySweepTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _conn;
    private readonly SqliteValidationCampaignStore _campaigns;
    private readonly SqliteInnovatedKnowledgeStore _innovated;
    private readonly SqliteProposalStore _proposals;
    private readonly SqliteGapStore _gaps;
    private readonly SqliteNodePacketStore _packets;

    public CampaignEligibilitySweepTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-sweep-{Guid.NewGuid():N}.db");
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

    private sealed class StubRouter : INodeRouter
    {
        // These fakes stand in for a node that IS available; the unavailable path has its own tests.
        public bool CanServe(string capability) => true;

        public Task<NodePacket> DispatchAsync(NodePacket packet, CancellationToken ct = default) => Task.FromResult(packet);
    }

    private sealed class FakeProtocolCritic : IProtocolCritic
    {
        public Task<ProtocolCritique> FalsifyAsync(ValidationCampaign campaign, CancellationToken ct = default)
            => Task.FromResult(new ProtocolCritique(System.Array.Empty<string>(), true, "ok"));
    }

    private CampaignCoordinator Coordinator() =>
        new(_campaigns, _innovated, _proposals, new StubRouter(), _packets, _gaps, new FakeProtocolCritic(),
            System.Array.Empty<INode>(), NullLogger<CampaignCoordinator>.Instance);

    private CampaignEligibilitySweep Sweep(bool enabled = true, int maxDrafts = 3) =>
        new(Coordinator(), _innovated, _campaigns,
            new CampaignEligibilityOptions { MinDistinctSuccesses = 2, MaxFailures = 1 },
            new CampaignSweepOptions { Enabled = enabled, MaxDraftsPerSweep = maxDrafts },
            NullLogger<CampaignEligibilitySweep>.Instance);

    private async Task<InnovatedKnowledgeRecord> SeedEntryAsync(string hypothesis = "combine A and B")
    {
        var rec = new InnovatedKnowledgeRecord { Hypothesis = hypothesis, Topic = "t", Intent = "i", Provenance = Provenance.Innovated, Confidence = Confidence.Of(0.3) };
        await _innovated.AddAsync(rec);
        return rec;
    }

    private async Task MakeEligibleAsync(InnovatedKnowledgeRecord entry, int successes = 2)
    {
        for (var i = 0; i < successes; i++)
        {
            var root = $"root-{entry.Id}-{i}";
            await _innovated.RecordConsumptionAsync(entry.Id, root);
            await _innovated.ResolveConsumptionAsync(entry.Id, root, ConsumptionOutcome.Success);
        }
    }

    [Fact]
    public async Task AutoDrafts_OnlyEligibleEntries_AtAutoDraftedPriority()
    {
        var eligible = await SeedEntryAsync("eligible hypothesis");
        await MakeEligibleAsync(eligible);
        var ineligible = await SeedEntryAsync("ineligible hypothesis");   // no successes → not eligible

        var drafted = await Sweep().RunOnceAsync();

        Assert.Equal(1, drafted);
        Assert.Empty(await _campaigns.GetByEntryAsync(ineligible.Id));

        var campaigns = await _campaigns.GetByEntryAsync(eligible.Id);
        var campaign = Assert.Single(campaigns);
        Assert.Equal(CampaignPriority.AutoDrafted, campaign.Priority);
        Assert.Equal(CampaignStatus.AwaitingAuthorization, campaign.Status);
    }

    [Fact]
    public async Task AutoDrafted_StillParksForHumanAuthorization_NeverAuthorizesOrPromotes()
    {
        var entry = await SeedEntryAsync();
        await MakeEligibleAsync(entry);

        await Sweep().RunOnceAsync();

        // The entry is untouched — still Innovated, no human-authored ledger events.
        var after = await _innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.Innovated, after!.Provenance);
        var revs = await _innovated.GetRevisionsAsync(entry.Id);
        Assert.DoesNotContain(revs, r => r.Kind == LedgerEventKind.HumanAuthorizeCampaign || r.Kind == LedgerEventKind.HumanConfirmPromotion);

        // An authorization proposal is pending and a packet is parked awaiting the human.
        var proposal = Assert.Single(await _proposals.GetPendingAsync());
        Assert.Equal(HumanProposalKind.AuthorizeCampaign, proposal.Kind);
        Assert.Equal(HumanProposalStatus.Pending, proposal.Status);
        Assert.NotNull(proposal.ParkedPacketId);
        var parked = await _packets.GetPacketAsync(proposal.ParkedPacketId!);
        Assert.Equal(NodeState.AwaitingDependency, parked!.State);
        Assert.Null(parked.LeaseExpiresAt);
    }

    [Fact]
    public async Task Dedupe_DoesNotDraftSecondCampaignForEntryWithActiveOne()
    {
        var entry = await SeedEntryAsync();
        await MakeEligibleAsync(entry);

        var first = await Sweep().RunOnceAsync();
        var second = await Sweep().RunOnceAsync();   // entry now has an active campaign

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(await _campaigns.GetByEntryAsync(entry.Id));
    }

    [Fact]
    public async Task Disabled_IsNoOp()
    {
        var entry = await SeedEntryAsync();
        await MakeEligibleAsync(entry);

        Assert.Equal(0, await Sweep(enabled: false).RunOnceAsync());
        Assert.Empty(await _campaigns.GetByEntryAsync(entry.Id));
    }

    [Fact]
    public async Task ThrottledByMaxDraftsPerSweep()
    {
        for (var i = 0; i < 3; i++) await MakeEligibleAsync(await SeedEntryAsync($"eligible {i}"));

        var drafted = await Sweep(maxDrafts: 2).RunOnceAsync();
        Assert.Equal(2, drafted);   // capped even though 3 were eligible
    }
}
