using Darci.Nodes;

namespace Darci.Nodes.Tests;

/// <summary>The simple priority work queue: highest priority first, FIFO within a tier. Behind IWorkScheduler
/// so a future resource-allocation scheduler is a drop-in replacement.</summary>
public class PriorityWorkQueueTests
{
    private static WorkItem Item(string id, CampaignPriority p) =>
        new(id, WorkKind.SurfaceAuthorization, p, $"work {id}");

    [Fact]
    public void HumanInitiated_ServedBeforeAutoDrafted()
    {
        IWorkScheduler q = new PriorityWorkQueue();
        q.Enqueue(Item("auto1", CampaignPriority.AutoDrafted));
        q.Enqueue(Item("human1", CampaignPriority.HumanInitiated));
        q.Enqueue(Item("auto2", CampaignPriority.AutoDrafted));
        q.Enqueue(Item("human2", CampaignPriority.HumanInitiated));

        Assert.Equal("human1", q.DequeueHighest()!.Id);   // both humans first...
        Assert.Equal("human2", q.DequeueHighest()!.Id);
        Assert.Equal("auto1", q.DequeueHighest()!.Id);    // ...then autos, in FIFO order
        Assert.Equal("auto2", q.DequeueHighest()!.Id);
        Assert.Null(q.DequeueHighest());
    }

    [Fact]
    public void FifoWithinTier()
    {
        IWorkScheduler q = new PriorityWorkQueue();
        q.Enqueue(Item("a", CampaignPriority.HumanInitiated));
        q.Enqueue(Item("b", CampaignPriority.HumanInitiated));
        q.Enqueue(Item("c", CampaignPriority.HumanInitiated));

        Assert.Equal("a", q.DequeueHighest()!.Id);
        Assert.Equal("b", q.DequeueHighest()!.Id);
        Assert.Equal("c", q.DequeueHighest()!.Id);
    }

    [Fact]
    public void Snapshot_IsPriorityOrdered_NonDestructive()
    {
        IWorkScheduler q = new PriorityWorkQueue();
        q.Enqueue(Item("auto", CampaignPriority.AutoDrafted));
        q.Enqueue(Item("human", CampaignPriority.HumanInitiated));

        var snap = q.Snapshot();
        Assert.Equal(new[] { "human", "auto" }, snap.Select(w => w.Id).ToArray());
        Assert.Equal(2, q.Count);   // snapshot did not consume
    }

    [Fact]
    public void Empty_DequeueReturnsNull()
        => Assert.Null(new PriorityWorkQueue().DequeueHighest());

    [Fact]
    public void Enqueue_StampsEnqueuedAt()
    {
        IWorkScheduler q = new PriorityWorkQueue();
        q.Enqueue(Item("x", CampaignPriority.HumanInitiated));
        Assert.NotEqual(default, q.DequeueHighest()!.EnqueuedAt);
    }
}
