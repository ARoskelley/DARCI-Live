using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>The Phase E §14c audit trail: capability-surface changes must be recorded, not assumed.</summary>
public sealed class NodeRegistrationStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteNodeRegistrationStore _store;

    public NodeRegistrationStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-reg-{Guid.NewGuid():N}.db");
        _store = new SqliteNodeRegistrationStore($"Data Source={_dbPath}", NullLogger<SqliteNodeRegistrationStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static NodeRegistrationRecord Record(string sha, params string[] caps) => new()
    {
        NodeId = NodeKeys.Coding,
        NodeVersion = "1.0.0",
        ContractVersion = NodeContractVersion.Current,
        ManifestSha256 = sha,
        Capabilities = caps,
        SourcePath = "nodes/darci.coding/darci-node.json",
    };

    [Fact]
    public async Task FirstRegistration_IsNotASurfaceChange()
    {
        var rec = await _store.RecordAsync(Record("AAA", Capabilities.CodingWrite));
        Assert.False(rec.SurfaceChanged);   // nothing to compare against yet
    }

    [Fact]
    public async Task SameManifestHash_IsNotASurfaceChange()
    {
        await _store.RecordAsync(Record("AAA", Capabilities.CodingWrite));
        var again = await _store.RecordAsync(Record("AAA", Capabilities.CodingWrite));
        Assert.False(again.SurfaceChanged);   // restarting the app must not look like a capability grant
    }

    [Fact]
    public async Task ChangedManifestHash_IsFlaggedAsASurfaceChange()
    {
        await _store.RecordAsync(Record("AAA", Capabilities.CodingWrite));
        var widened = await _store.RecordAsync(Record("BBB", Capabilities.CodingWrite, Capabilities.CodingTest));

        Assert.True(widened.SurfaceChanged);   // ← the §14c audit signal
        var history = await _store.GetHistoryAsync(NodeKeys.Coding);
        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { false, true }, history.Select(h => h.SurfaceChanged).ToArray());
        Assert.Equal(new[] { Capabilities.CodingWrite, Capabilities.CodingTest }, history[1].Capabilities);
    }

    [Fact]
    public async Task GetLatest_ReturnsOneRowPerNode_MostRecent()
    {
        await _store.RecordAsync(Record("AAA", Capabilities.CodingWrite));
        await _store.RecordAsync(Record("BBB", Capabilities.CodingWrite, Capabilities.CodingTest));
        await _store.RecordAsync(Record("CCC") with { NodeId = NodeKeys.Innovation, Capabilities = new[] { Capabilities.InnovationSynthesize } });

        var latest = await _store.GetLatestAsync();
        Assert.Equal(2, latest.Count);
        var coding = latest.Single(l => l.NodeId == NodeKeys.Coding);
        Assert.Equal("BBB", coding.ManifestSha256);
        Assert.Equal(new[] { Capabilities.CodingWrite, Capabilities.CodingTest }, coding.Capabilities);
        Assert.Equal("nodes/darci.coding/darci-node.json", coding.SourcePath);
    }
}
