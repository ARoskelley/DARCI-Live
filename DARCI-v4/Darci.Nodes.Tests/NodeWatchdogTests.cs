using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

/// <summary>
/// The orphaning-killer tests. These prove that a packet can never stay stuck active: an expired
/// lease or a process restart always drives it to a terminal Aborted state.
/// </summary>
public sealed class NodeWatchdogTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteNodePacketStore _store;
    private readonly NodeWatchdog _watchdog;

    public NodeWatchdogTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-watchdog-{Guid.NewGuid():N}.db");
        _store = new SqliteNodePacketStore($"Data Source={_dbPath}", NullLogger<SqliteNodePacketStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
        _watchdog = new NodeWatchdog(_store, NullLogger<NodeWatchdog>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static NodePacket WorkingPacket(DateTime start, TimeSpan lease)
    {
        return NodePacket.Create("stuck task", address: NodeId.Coding)
            .Transition(NodeId.Coding, NodeState.Routed, "routed", nowUtc: start)
            .Transition(NodeId.Coding, NodeState.Accepted, "accepted", nowUtc: start)
            .Transition(NodeId.Coding, NodeState.Working, "working", leaseFor: lease, nowUtc: start);
    }

    [Fact]
    public async Task ExpiredLease_IsAborted()
    {
        var start = DateTime.UtcNow.AddMinutes(-30);
        var packet = WorkingPacket(start, TimeSpan.FromMinutes(5)); // lease expired 25 min ago
        await _store.CreatePacketAsync(packet);

        var reaped = await _watchdog.SweepExpiredLeasesAsync(DateTime.UtcNow);

        Assert.Equal(1, reaped);
        var loaded = await _store.GetPacketAsync(packet.Id);
        Assert.Equal(NodeState.Aborted, loaded!.State);
        Assert.True(loaded.State.IsTerminal());
        Assert.Equal(false, loaded.LastEntry!.Success);
        Assert.Contains("Lease expired", loaded.LastEntry.Error);
        Assert.Null(loaded.LeaseExpiresAt); // terminal drops the lease
    }

    [Fact]
    public async Task LiveLease_IsLeftAlone()
    {
        var packet = WorkingPacket(DateTime.UtcNow, TimeSpan.FromMinutes(30)); // lease valid 30 min out
        await _store.CreatePacketAsync(packet);

        var reaped = await _watchdog.SweepExpiredLeasesAsync(DateTime.UtcNow);

        Assert.Equal(0, reaped);
        var loaded = await _store.GetPacketAsync(packet.Id);
        Assert.Equal(NodeState.Working, loaded!.State); // still working — untouched
    }

    [Fact]
    public async Task TerminalPacket_IsNeverReaped()
    {
        var start = DateTime.UtcNow.AddMinutes(-30);
        var done = WorkingPacket(start, TimeSpan.FromMinutes(5))
            .Transition(NodeId.Coding, NodeState.Succeeded, "done", nowUtc: start.AddMinutes(1));
        await _store.CreatePacketAsync(done);

        // Even with a long-expired lease timestamp in history, a terminal packet is not active.
        var reaped = await _watchdog.SweepExpiredLeasesAsync(DateTime.UtcNow);

        Assert.Equal(0, reaped);
        var loaded = await _store.GetPacketAsync(done.Id);
        Assert.Equal(NodeState.Succeeded, loaded!.State);
        Assert.Equal(4, loaded.Log.Count); // not mutated by the watchdog
    }

    [Fact]
    public async Task StartupSweep_AbortsAllActivePackets_RegardlessOfLease()
    {
        // Simulates a crash: a packet was left Working with a lease that has NOT yet expired.
        // On restart nothing is running it, so the startup sweep must still reap it.
        var freshLease = WorkingPacket(DateTime.UtcNow, TimeSpan.FromHours(1));
        var noLease = NodePacket.Create("parked", address: NodeId.Coding)
            .Transition(NodeId.Coding, NodeState.Routed, "routed"); // active, never leased
        var alreadyDone = WorkingPacket(DateTime.UtcNow.AddMinutes(-10), TimeSpan.FromMinutes(5))
            .Transition(NodeId.Coding, NodeState.Failed, "failed");
        await _store.CreatePacketAsync(freshLease);
        await _store.CreatePacketAsync(noLease);
        await _store.CreatePacketAsync(alreadyDone);

        var reaped = await _watchdog.SweepStartupOrphansAsync();

        Assert.Equal(2, reaped); // both active ones, regardless of lease state
        Assert.Equal(NodeState.Aborted, (await _store.GetPacketAsync(freshLease.Id))!.State);
        Assert.Equal(NodeState.Aborted, (await _store.GetPacketAsync(noLease.Id))!.State);
        Assert.Equal(NodeState.Failed, (await _store.GetPacketAsync(alreadyDone.Id))!.State); // terminal left intact
    }

    [Fact]
    public async Task Sweep_IsIdempotent()
    {
        var start = DateTime.UtcNow.AddMinutes(-30);
        var packet = WorkingPacket(start, TimeSpan.FromMinutes(5));
        await _store.CreatePacketAsync(packet);

        var first = await _watchdog.SweepExpiredLeasesAsync(DateTime.UtcNow);
        var second = await _watchdog.SweepExpiredLeasesAsync(DateTime.UtcNow);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // already terminal — nothing left to reap
    }
}
