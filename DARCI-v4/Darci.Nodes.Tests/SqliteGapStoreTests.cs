using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

public sealed class SqliteGapStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteGapStore _store;

    public SqliteGapStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-gaps-{Guid.NewGuid():N}.db");
        _store = new SqliteGapStore($"Data Source={_dbPath}", NullLogger<SqliteGapStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static GapRecord Sample(string status = GapStatus.Deferred, string corr = "corr-1") => new()
    {
        CorrelationId = corr,
        OriginPacketId = "pkt-1",
        OriginNode = NodeId.Knowledge,
        Question = "What is the Damm table?",
        Intent = "implement a Damm checksum",
        Missing = "the exact quasigroup table",
        Confidence = Confidence.Of(0.3),
        Status = status,
    };

    [Fact]
    public async Task AddAndGet_RoundTrips_WithFullContext()
    {
        var gap = Sample();
        await _store.AddAsync(gap);

        var loaded = await _store.GetAsync(gap.Id);
        Assert.NotNull(loaded);
        Assert.Equal("What is the Damm table?", loaded!.Question);
        Assert.Equal("implement a Damm checksum", loaded.Intent);     // intent retained for future ideation node
        Assert.Equal("the exact quasigroup table", loaded.Missing);
        Assert.Equal("corr-1", loaded.CorrelationId);                 // traceable
        Assert.Equal(NodeId.Knowledge, loaded.OriginNode);
        Assert.Equal(0.3, loaded.Confidence.Score, 5);
    }

    [Fact]
    public async Task GetByStatus_FiltersAndIsRetrievableByTheLoop()
    {
        await _store.AddAsync(Sample(GapStatus.GoalCreated, "c1"));
        await _store.AddAsync(Sample(GapStatus.GoalCreated, "c2"));
        await _store.AddAsync(Sample(GapStatus.Filling, "c3"));

        var deferred = await _store.GetByStatusAsync(GapStatus.GoalCreated);
        Assert.Equal(2, deferred.Count);
        Assert.All(deferred, g => Assert.Equal(GapStatus.GoalCreated, g.Status));
    }

    [Fact]
    public async Task Update_PersistsGoalLink()
    {
        var gap = Sample();
        await _store.AddAsync(gap);
        await _store.UpdateAsync(gap with { Status = GapStatus.GoalCreated, GoalId = "42" });

        var loaded = await _store.GetAsync(gap.Id);
        Assert.Equal(GapStatus.GoalCreated, loaded!.Status);
        Assert.Equal("42", loaded.GoalId);
    }

    [Fact]
    public async Task GetByCorrelation_GroupsGaps()
    {
        await _store.AddAsync(Sample(corr: "shared"));
        await _store.AddAsync(Sample(corr: "shared"));
        await _store.AddAsync(Sample(corr: "other"));

        var grouped = await _store.GetByCorrelationAsync("shared");
        Assert.Equal(2, grouped.Count);
    }
}
