using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

public sealed class SqliteNodePacketStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _conn;
    private readonly SqliteNodePacketStore _store;

    public SqliteNodePacketStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-nodes-{Guid.NewGuid():N}.db");
        _conn = $"Data Source={_dbPath}";
        _store = new SqliteNodePacketStore(_conn, NullLogger<SqliteNodePacketStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task CreateAndGet_RoundTripsHeaderAndLog()
    {
        var packet = NodePacket.Create("implement Levenshtein", "all tests pass",
            address: NodeId.Coding, slots: new Dictionary<string, string> { ["workspaceId"] = "ws1" })
            .Transition(NodeId.Coding, NodeState.Routed, "routed to coding");

        await _store.CreatePacketAsync(packet);

        var loaded = await _store.GetPacketAsync(packet.Id);
        Assert.NotNull(loaded);
        Assert.Equal("implement Levenshtein", loaded!.Payload.Intent);
        Assert.Equal("all tests pass", loaded.Payload.SuccessCriteria);
        Assert.Equal("ws1", loaded.Payload.Slot("workspaceId"));
        Assert.Equal(NodeId.Coding, loaded.Address);
        Assert.Equal(NodeState.Routed, loaded.State);
        Assert.Single(loaded.Log);
        Assert.Equal("routed to coding", loaded.Log[0].Decision);
    }

    [Fact]
    public async Task Save_IsAppendOnly_AndPreservesLogOrder()
    {
        var p0 = NodePacket.Create("task", address: NodeId.Coding)
            .Transition(NodeId.Coding, NodeState.Routed, "step1");
        await _store.CreatePacketAsync(p0);

        var p1 = p0
            .Transition(NodeId.Coding, NodeState.Accepted, "step2")
            .Transition(NodeId.Coding, NodeState.Working, "step3", leaseFor: TimeSpan.FromMinutes(5));
        await _store.SavePacketAsync(p1);

        // Saving again with no new entries must not duplicate rows.
        await _store.SavePacketAsync(p1);

        var loaded = await _store.GetPacketAsync(p0.Id);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Log.Count);
        Assert.Equal(new[] { "step1", "step2", "step3" }, loaded.Log.Select(e => e.Decision).ToArray());
        Assert.Equal(NodeState.Working, loaded.State);
        Assert.NotNull(loaded.LeaseExpiresAt);
    }

    [Fact]
    public async Task Confidence_PersistsThroughLog()
    {
        var p = NodePacket.Create("task", address: NodeId.Coding)
            .Transition(NodeId.Coding, NodeState.Routed, "low confidence step",
                confidence: Confidence.Of(0.2, "unsure"));
        await _store.CreatePacketAsync(p);

        var loaded = await _store.GetPacketAsync(p.Id);
        var entry = loaded!.Log[0];
        Assert.Equal(0.2, entry.Confidence.Score, 5);
        Assert.Equal("unsure", entry.Confidence.Note);
        Assert.True(entry.Confidence.IsLow);
    }

    [Fact]
    public async Task GetByStates_FiltersByState()
    {
        var working = NodePacket.Create("w", address: NodeId.Coding)
            .Transition(NodeId.Coding, NodeState.Routed, "r")
            .Transition(NodeId.Coding, NodeState.Accepted, "a")
            .Transition(NodeId.Coding, NodeState.Working, "w");
        var done = NodePacket.Create("d", address: NodeId.Coding)
            .Transition(NodeId.Coding, NodeState.Routed, "r")
            .Transition(NodeId.Coding, NodeState.Accepted, "a")
            .Transition(NodeId.Coding, NodeState.Working, "w")
            .Transition(NodeId.Coding, NodeState.Succeeded, "done");
        await _store.CreatePacketAsync(working);
        await _store.CreatePacketAsync(done);

        var inProgress = await _store.GetByStatesAsync(new[] { NodeState.Working });
        Assert.Single(inProgress);
        Assert.Equal(working.Id, inProgress[0].Id);

        var terminal = await _store.GetByStatesAsync(new[] { NodeState.Succeeded, NodeState.Failed });
        Assert.Single(terminal);
        Assert.Equal(done.Id, terminal[0].Id);
    }

    [Fact]
    public async Task GetByCorrelation_GroupsParentAndChild()
    {
        var parent = NodePacket.Create("parent", address: NodeId.Coding);
        var child = NodePacket.Create("child", address: NodeId.Knowledge, correlationId: parent.CorrelationId);
        await _store.CreatePacketAsync(parent);
        await _store.CreatePacketAsync(child);

        var group = await _store.GetByCorrelationAsync(parent.CorrelationId);
        Assert.Equal(2, group.Count);
        Assert.Contains(group, p => p.Id == parent.Id);
        Assert.Contains(group, p => p.Id == child.Id);
    }

    [Fact]
    public async Task GetStatus_ReturnsPollableProjection()
    {
        var p = NodePacket.Create("task", address: NodeId.Coding)
            .Transition(NodeId.Coding, NodeState.Routed, "routed", confidence: Confidence.Of(0.8));
        await _store.CreatePacketAsync(p);

        var status = await _store.GetStatusAsync(p.Id);
        Assert.NotNull(status);
        Assert.Equal(NodeState.Routed, status!.State);
        Assert.False(status.IsTerminal);
        Assert.Equal("routed", status.LastDecision);
        Assert.Equal(1, status.LogEntryCount);
    }
}
