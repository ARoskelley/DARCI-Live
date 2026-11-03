#nullable enable

namespace Darci.Nodes;

/// <summary>A unit of schedulable work. Today it just carries a priority tier and enough context to surface
/// it; the fields that a future resource-allocation scheduler needs (which <see cref="Capability"/> /
/// environment it wants, its correlation) travel with it now so that scheduler is a drop-in replacement.</summary>
public sealed record WorkItem(
    string Id,
    WorkKind Kind,
    CampaignPriority Priority,
    string Description,
    string? CampaignId = null,
    string? CorrelationId = null,
    Capability? Capability = null,
    DateTime EnqueuedAt = default);

/// <summary>What a work item represents. Kept coarse for now; the future scheduler can refine it.</summary>
public enum WorkKind
{
    SurfaceAuthorization = 0,  // an authorization proposal is waiting for a human — surface it (priority-ordered)
    DispatchStep = 1,          // a validation step is ready to run at some environment
}

/// <summary>
/// A priority-ordered work queue. Today it is a simple in-memory queue that serves higher-priority work
/// first (human-initiated before auto-drafted), FIFO within a tier. It is deliberately behind this
/// interface so a future RESOURCE-ALLOCATION SCHEDULER — one that decides which node runs what, when, and
/// whether to preempt/overwrite a running task under compute scarcity — can implement the same contract
/// without touching callers. See docs/INNOVATION_NODE_DESIGN.md §16.
/// </summary>
public interface IWorkScheduler
{
    /// <summary>Enqueue work at its <see cref="WorkItem.Priority"/>.</summary>
    void Enqueue(WorkItem work);

    /// <summary>Remove and return the highest-priority item (FIFO within a tier), or null if empty.</summary>
    WorkItem? DequeueHighest();

    /// <summary>Non-destructive, priority-ordered view (highest first) — for inspection / surfacing.</summary>
    IReadOnlyList<WorkItem> Snapshot();

    int Count { get; }
}

/// <summary>Simple thread-safe in-memory priority queue. Highest <see cref="CampaignPriority"/> first, then
/// FIFO by enqueue order (a monotonic sequence breaks ties so equal-priority work is fair).</summary>
public sealed class PriorityWorkQueue : IWorkScheduler
{
    private readonly object _gate = new();
    private readonly List<(long Seq, WorkItem Item)> _items = new();
    private long _seq;

    public void Enqueue(WorkItem work)
    {
        var item = work.EnqueuedAt == default ? work with { EnqueuedAt = DateTime.UtcNow } : work;
        lock (_gate) _items.Add((_seq++, item));
    }

    public WorkItem? DequeueHighest()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;
            var bestIdx = 0;
            for (var i = 1; i < _items.Count; i++)
                if (IsHigher(_items[i], _items[bestIdx])) bestIdx = i;
            var item = _items[bestIdx].Item;
            _items.RemoveAt(bestIdx);
            return item;
        }
    }

    public IReadOnlyList<WorkItem> Snapshot()
    {
        lock (_gate)
            return _items
                .OrderByDescending(x => (int)x.Item.Priority)
                .ThenBy(x => x.Seq)
                .Select(x => x.Item)
                .ToList();
    }

    public int Count { get { lock (_gate) return _items.Count; } }

    // Higher priority wins; on a tie the earlier sequence (FIFO) wins.
    private static bool IsHigher((long Seq, WorkItem Item) a, (long Seq, WorkItem Item) b)
    {
        var pa = (int)a.Item.Priority;
        var pb = (int)b.Item.Priority;
        if (pa != pb) return pa > pb;
        return a.Seq < b.Seq;
    }
}
