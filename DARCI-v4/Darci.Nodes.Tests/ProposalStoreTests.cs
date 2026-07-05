using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

public sealed class ProposalStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteProposalStore _store;

    public ProposalStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-proposals-{Guid.NewGuid():N}.db");
        _store = new SqliteProposalStore($"Data Source={_dbPath}", NullLogger<SqliteProposalStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static HumanProposal Sample(string subject = "entry-1", string? parked = null) => new()
    {
        CorrelationId = "corr-1",
        Kind = HumanProposalKind.PromoteInnovated,
        SubjectId = subject,
        TargetProvenance = Provenance.HumanApproved,
        Title = "Promote hypothesis X",
        Summary = "combine A and B",
        JustificationJson = "{\"evidence\":1}",
        ParkedPacketId = parked,
    };

    [Fact]
    public async Task AddAndGet_RoundTrips()
    {
        var p = Sample();
        await _store.AddAsync(p);
        var loaded = await _store.GetAsync(p.Id);
        Assert.NotNull(loaded);
        Assert.Equal(HumanProposalStatus.Pending, loaded!.Status);
        Assert.Equal("entry-1", loaded.SubjectId);
        Assert.Equal(Provenance.HumanApproved, loaded.TargetProvenance);
    }

    [Fact]
    public async Task GetPending_ExcludesDecided()
    {
        var a = Sample("a");
        var b = Sample("b");
        await _store.AddAsync(a);
        await _store.AddAsync(b);
        await _store.RecordDecisionAsync(a.Id, HumanProposalStatus.Approved, "tinman", "ok");

        var pending = await _store.GetPendingAsync();
        Assert.Single(pending);
        Assert.Equal(b.Id, pending[0].Id);
    }

    [Fact]
    public async Task RecordDecision_IsIdempotent()
    {
        var p = Sample();
        await _store.AddAsync(p);
        Assert.True(await _store.RecordDecisionAsync(p.Id, HumanProposalStatus.Approved, "t", null));
        Assert.False(await _store.RecordDecisionAsync(p.Id, HumanProposalStatus.Rejected, "t", null)); // already decided
        Assert.Equal(HumanProposalStatus.Approved, (await _store.GetAsync(p.Id))!.Status);
    }

    [Fact]
    public async Task HasPendingForParkedPacket_TracksLivePendingOnly()
    {
        var p = Sample(parked: "pkt-42");
        await _store.AddAsync(p);
        Assert.True(await _store.HasPendingForParkedPacketAsync("pkt-42"));

        await _store.RecordDecisionAsync(p.Id, HumanProposalStatus.Rejected, "t", null);
        Assert.False(await _store.HasPendingForParkedPacketAsync("pkt-42"));   // decided → no longer live
        Assert.False(await _store.HasPendingForParkedPacketAsync("other"));
    }
}
