using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

/// <summary>Phase C: parking, restart-survival, and the human decision path (the one route above the cap).</summary>
public sealed class HumanGateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _conn;
    private readonly SqliteProposalStore _proposals;
    private readonly SqliteInnovatedKnowledgeStore _innovated;
    private readonly SqliteNodePacketStore _packets;

    public HumanGateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-humangate-{Guid.NewGuid():N}.db");
        _conn = $"Data Source={_dbPath}";
        _proposals = new SqliteProposalStore(_conn, NullLogger<SqliteProposalStore>.Instance);
        _innovated = new SqliteInnovatedKnowledgeStore(_conn, NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        _packets = new SqliteNodePacketStore(_conn, NullLogger<SqliteNodePacketStore>.Instance);
        _proposals.InitializeAsync().GetAwaiter().GetResult();
        _innovated.InitializeAsync().GetAwaiter().GetResult();
        _packets.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private HumanGateService Gate() =>
        new(_proposals, _innovated, _packets, NullLogger<HumanGateService>.Instance);

    private async Task<InnovatedKnowledgeRecord> SeedInnovatedAsync()
    {
        var rec = new InnovatedKnowledgeRecord
        {
            CorrelationId = "corr-1", Hypothesis = "combine A and B", Topic = "t", Intent = "i",
            Provenance = Provenance.Innovated, Confidence = Confidence.Of(0.3),
        };
        await _innovated.AddAsync(rec);
        return rec;
    }

    private async Task<HumanProposal> FilePromotionAsync(string subjectId, string? parked = null)
    {
        var p = new HumanProposal
        {
            CorrelationId = "corr-1", Kind = HumanProposalKind.PromoteInnovated,
            SubjectId = subjectId, TargetProvenance = Provenance.HumanApproved,
            Title = "Promote", Summary = "A+B", ParkedPacketId = parked,
        };
        await _proposals.AddAsync(p);
        return p;
    }

    // ── Park + restart survival ──

    private static NodePacket Parked() =>
        NodePacket.Create("await human", capability: Capability.Innovate)
            .Transition(NodeId.Innovation, NodeState.Routed, "r")
            .Transition(NodeId.Innovation, NodeState.Accepted, "a")
            .Transition(NodeId.Innovation, NodeState.Working, "w", leaseFor: TimeSpan.FromMinutes(5))
            .ParkAwaitingDependency(NodeId.Innovation, "awaiting human approval");

    [Fact]
    public void Park_EntersAwaitingDependency_AndClearsLease()
    {
        var parked = Parked();
        Assert.Equal(NodeState.AwaitingDependency, parked.State);
        Assert.Null(parked.LeaseExpiresAt);   // no lease held while the human is away
        Assert.False(parked.IsLeaseExpired(DateTime.UtcNow.AddYears(1)));  // never a watchdog target via lease
    }

    [Fact]
    public async Task ParkedProposal_SurvivesStartupSweep()
    {
        var parked = Parked();
        await _packets.CreatePacketAsync(parked);
        await FilePromotionAsync("entry-x", parked: parked.Id);   // live pending proposal for this packet

        var watchdog = new NodeWatchdog(_packets, NullLogger<NodeWatchdog>.Instance, _proposals);
        var reaped = await watchdog.SweepStartupOrphansAsync();

        Assert.Equal(0, reaped);                                  // carve-out: not reaped
        Assert.Equal(NodeState.AwaitingDependency, (await _packets.GetPacketAsync(parked.Id))!.State);
    }

    [Fact]
    public async Task ParkedPacket_WithoutPendingProposal_IsReaped()
    {
        var parked = Parked();
        await _packets.CreatePacketAsync(parked);   // no proposal filed

        var watchdog = new NodeWatchdog(_packets, NullLogger<NodeWatchdog>.Instance, _proposals);
        var reaped = await watchdog.SweepStartupOrphansAsync();

        Assert.Equal(1, reaped);                    // an orphan (nothing is waiting on it)
        Assert.Equal(NodeState.Aborted, (await _packets.GetPacketAsync(parked.Id))!.State);
    }

    // ── The decision path ──

    [Fact]
    public async Task Approve_PromotesAboveCap_ViaHumanLedgerEvent()
    {
        var entry = await SeedInnovatedAsync();
        var proposal = await FilePromotionAsync(entry.Id);

        var result = await Gate().DecideAsync(proposal.Id, approve: true, note: "looks sound", decidedBy: "tinman");

        Assert.True(result.Applied);
        var promoted = await _innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.HumanApproved, promoted!.Provenance);            // above the innovation cap
        Assert.True(promoted.Confidence.Score > ProvenancePolicy.InnovatedCap);  // trust actually rose
        Assert.False(promoted.Confidence.IsLow);

        // The rise was recorded as a human-authored ledger event (what the invariant guard requires).
        var revs = await _innovated.GetRevisionsAsync(entry.Id);
        Assert.Contains(revs, r => r.Kind == LedgerEventKind.HumanConfirmPromotion);
        Assert.True(Ledger.IsHumanAuthored(revs[^1].Kind));

        Assert.Equal(HumanProposalStatus.Approved, (await _proposals.GetAsync(proposal.Id))!.Status);
    }

    [Fact]
    public async Task Reject_LeavesEntryCapped_AndLogsHumanReject()
    {
        var entry = await SeedInnovatedAsync();
        var proposal = await FilePromotionAsync(entry.Id);

        var result = await Gate().DecideAsync(proposal.Id, approve: false, note: "not yet", decidedBy: "tinman");

        Assert.True(result.Applied);
        var after = await _innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.Innovated, after!.Provenance);         // stays capped
        Assert.True(after.Confidence.IsLow);

        var revs = await _innovated.GetRevisionsAsync(entry.Id);
        Assert.Contains(revs, r => r.Kind == LedgerEventKind.HumanReject);
        Assert.Equal(HumanProposalStatus.Rejected, (await _proposals.GetAsync(proposal.Id))!.Status);
    }

    [Fact]
    public async Task Decide_ResumesParkedPacket()
    {
        var entry = await SeedInnovatedAsync();
        var parked = Parked();
        await _packets.CreatePacketAsync(parked);
        var proposal = await FilePromotionAsync(entry.Id, parked: parked.Id);

        await Gate().DecideAsync(proposal.Id, approve: true, note: null, decidedBy: "tinman");

        var resumed = await _packets.GetPacketAsync(parked.Id);
        Assert.Equal(NodeState.Succeeded, resumed!.State);            // dependency resolved → released
    }

    [Fact]
    public async Task ListPending_AndDoubleDecide()
    {
        var entry = await SeedInnovatedAsync();
        var p = await FilePromotionAsync(entry.Id);
        var gate = Gate();

        Assert.Single(await gate.ListPendingAsync());

        Assert.True((await gate.DecideAsync(p.Id, true, null, "t")).Applied);
        var second = await gate.DecideAsync(p.Id, true, null, "t");
        Assert.False(second.Applied);                                 // already decided
        Assert.Empty(await gate.ListPendingAsync());
    }

    [Fact]
    public async Task Decide_UnknownProposal_ReturnsNotApplied()
    {
        var result = await Gate().DecideAsync("nope", true, null, "t");
        Assert.False(result.Applied);
    }
}
