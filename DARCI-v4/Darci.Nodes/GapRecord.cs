#nullable enable

namespace Darci.Nodes;

/// <summary>Lifecycle status of a knowledge gap.</summary>
public static class GapStatus
{
    public const string Open = "open";
    public const string Filling = "filling";        // an immediate fill is in flight
    public const string Deferred = "deferred";       // parked for later (pre-goal)
    public const string GoalCreated = "goal-created"; // a living-loop goal was generated for it
    public const string Filled = "filled";
}

/// <summary>
/// A persisted record of a knowledge gap a node could not resolve. Carries enough context — the
/// question, the originating intent, and what was missing — to (a) feed DARCI's longer-term learning,
/// (b) trace back to the packet that produced it, and (c) serve as raw material for a future
/// ideation/theorizing node. Stored in SQLite alongside the packet log (decision 5).
/// </summary>
public sealed record GapRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Correlation id of the packet that produced the gap (traceability + grouping).</summary>
    public string CorrelationId { get; init; } = "";
    public string OriginPacketId { get; init; } = "";
    public NodeId OriginNode { get; init; }

    /// <summary>The knowledge question being pursued.</summary>
    public string Question { get; init; } = "";
    /// <summary>The originating goal/intent behind the question (raw material for later theorizing).</summary>
    public string Intent { get; init; } = "";
    /// <summary>What specifically was missing / unanswered.</summary>
    public string Missing { get; init; } = "";

    public Confidence Confidence { get; init; } = Confidence.Unassessed;

    public string Status { get; init; } = GapStatus.Open;

    /// <summary>The living-loop goal generated for this gap (deferred path), if any.</summary>
    public string? GoalId { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
