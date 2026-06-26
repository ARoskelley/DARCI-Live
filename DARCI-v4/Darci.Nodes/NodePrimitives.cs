#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Identifies a node — a specialized "space" (environment / agent / model) that work can be
/// routed to. Phase 0 only exercises <see cref="Coding"/>, but the full set is declared up front
/// so the envelope schema already accommodates the engineering / knowledge / biomed nodes that
/// later phases plug in (decision 2: build routing generically, not coding-specific).
/// </summary>
public enum NodeId
{
    Orchestrator = 0,
    Living = 1,
    Coding = 2,
    Engineering = 3,
    Knowledge = 4,   // the KG / deep-research node
    Cad = 5,
}

/// <summary>
/// A capability a node advertises. Routing may target an explicit <see cref="NodeId"/> or request a
/// capability and let the router resolve the node (decision 3: mechanism is open, but the packet must
/// reach the right node with the info it needs).
/// </summary>
public enum Capability
{
    WriteCode = 0,
    RunTests = 1,
    DesignGeometry = 2,
    GenerateCad = 3,
    AnswerKnowledge = 4,
    FillKnowledgeGap = 5,
}

/// <summary>
/// The lifecycle state of a packet at a node. This is the state machine that makes orphaning
/// structurally impossible: every packet is always in exactly one of these, transitions are
/// validated (<see cref="NodeStateMachine"/>), and a non-terminal packet always either holds a live
/// lease or gets swept to <see cref="Aborted"/> by the watchdog.
/// </summary>
public enum NodeState
{
    Created = 0,             // envelope minted, not yet routed
    Routed = 1,              // addressed to a node, awaiting pickup
    Accepted = 2,            // a node took ownership
    Working = 3,             // a node is actively processing (holds a lease)
    AwaitingDependency = 4,  // blocked on a child packet at another node
    Succeeded = 5,           // terminal — work completed and verified
    Failed = 6,              // terminal — work completed but did not succeed
    Aborted = 7,             // terminal — killed by watchdog / crash recovery / cancellation
}

/// <summary>State-machine helpers. Terminal states have no outgoing transitions.</summary>
public static class NodeStateMachine
{
    public static bool IsTerminal(this NodeState s) =>
        s is NodeState.Succeeded or NodeState.Failed or NodeState.Aborted;

    /// <summary>Active = non-terminal. An active packet must be covered by a lease or the watchdog.</summary>
    public static bool IsActive(this NodeState s) => !s.IsTerminal();

    /// <summary>Whether <paramref name="to"/> is a legal next state from <paramref name="from"/>.</summary>
    public static bool CanTransition(NodeState from, NodeState to)
    {
        if (from.IsTerminal()) return false;          // terminal is forever
        if (from == to) return true;                  // idempotent re-write of same state is allowed

        // Anything active can always fail or be aborted — a node may reject at routing/acceptance, the
        // watchdog may reap at any active point, and resolution can fail before work starts.
        if (to is NodeState.Aborted or NodeState.Failed) return true;

        // Succeeded requires having actually worked.
        return from switch
        {
            NodeState.Created => to is NodeState.Routed,
            NodeState.Routed => to is NodeState.Accepted,
            NodeState.Accepted => to is NodeState.Working,
            NodeState.Working => to is NodeState.AwaitingDependency or NodeState.Succeeded,
            NodeState.AwaitingDependency => to is NodeState.Working or NodeState.Succeeded,
            _ => false,
        };
    }
}

/// <summary>
/// Unified confidence value (decision 6). One type replaces the four scattered representations
/// (<c>CodingTaskRecord.ConfidenceScore</c>, <c>ResearchOutcome.Confidence</c>,
/// <c>KnowledgeAssessment.GraphConfidence</c>, the RL reward). <see cref="Score"/> is in [0,1];
/// <see cref="Unassessed"/> (-1) is the sentinel for "not yet evaluated", which is itself a gap.
/// Gap detection is preserved via <see cref="IsLow"/> / <see cref="IsGap"/>.
/// </summary>
public readonly record struct Confidence(double Score, string? Note = null)
{
    public const double LowThreshold = 0.4;

    public static Confidence Unassessed => new(-1.0, null);

    public bool IsAssessed => Score >= 0;

    /// <summary>Assessed but below the action threshold — a recognised low-confidence state.</summary>
    public bool IsLow => IsAssessed && Score < LowThreshold;

    /// <summary>A knowledge gap: either never assessed, or assessed low. Drives escalation/routing.</summary>
    public bool IsGap => !IsAssessed || IsLow;

    public static Confidence Of(double score, string? note = null) =>
        new(score < 0 ? -1.0 : Math.Clamp(score, 0.0, 1.0), note);
}

/// <summary>
/// One append-only entry in a packet's decision log. This is the audit trail and the substrate for
/// system-wide learning (decision 3 + 5): every decision, the confidence behind it, and whether it
/// worked is recorded with the node that made it and the artifacts it owns.
/// </summary>
public sealed record NodeLogEntry
{
    public NodeId Node { get; init; }
    public DateTime At { get; init; } = DateTime.UtcNow;
    public NodeState StateAfter { get; init; }
    public string Decision { get; init; } = "";
    public Confidence Confidence { get; init; } = Confidence.Unassessed;

    /// <summary>null while the step is in flight; true/false once it resolves.</summary>
    public bool? Success { get; init; }

    public string? Error { get; init; }

    /// <summary>Paths/identifiers this entry is authoritative for (e.g. files a node wrote).</summary>
    public IReadOnlyList<string> Artifacts { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The progressively-disclosed payload. <see cref="Intent"/> and <see cref="SuccessCriteria"/> are
/// set at creation and never rewritten, so every node sees the true original goal. <see cref="Slots"/>
/// is the extensible area each node reads from and tacks onto (decision 4: structured, parseable —
/// usable by non-language models, not prose).
/// </summary>
public sealed record PacketPayload
{
    public string Intent { get; init; } = "";
    public string? SuccessCriteria { get; init; }
    public IReadOnlyDictionary<string, string> Slots { get; init; } =
        new Dictionary<string, string>();

    public string? Slot(string key) => Slots.TryGetValue(key, out var v) ? v : null;

    /// <summary>Returns a new payload with the slot set/overwritten. Immutable copy.</summary>
    public PacketPayload WithSlot(string key, string value)
    {
        var next = new Dictionary<string, string>(Slots) { [key] = value };
        return this with { Slots = next };
    }
}
