#nullable enable

namespace Darci.Nodes;

/// <summary>
/// The envelope that travels between nodes. Immutable: every mutation returns a new instance with an
/// appended log entry, so the <see cref="Log"/> is genuinely append-only. Persisted in SQLite
/// (decision 5) and keyed by <see cref="CorrelationId"/> for later retrieval / learning (decision 3).
/// </summary>
public sealed record NodePacket
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Groups a packet with any child packets it spawns at other nodes.</summary>
    public string CorrelationId { get; init; } = "";

    /// <summary>The node that should act next. null = back to the orchestrator.</summary>
    public NodeId? Address { get; init; }

    /// <summary>Alternative to <see cref="Address"/>: request a capability, let the router resolve.</summary>
    public Capability? RequestedCapability { get; init; }

    public NodeState State { get; init; } = NodeState.Created;
    public PacketPayload Payload { get; init; } = new();
    public IReadOnlyList<NodeLogEntry> Log { get; init; } = Array.Empty<NodeLogEntry>();

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// While a node is actively working, it holds a lease that expires at this time. A crashed or hung
    /// node leaves an expired lease, which the watchdog reaps. null = no active lease (terminal, or
    /// parked awaiting a dependency).
    /// </summary>
    public DateTime? LeaseExpiresAt { get; init; }

    /// <summary>The most recent log entry, or null if none recorded yet.</summary>
    public NodeLogEntry? LastEntry => Log.Count > 0 ? Log[^1] : null;

    /// <summary>Mint a fresh packet for an intent. Starts in <see cref="NodeState.Created"/>.</summary>
    public static NodePacket Create(
        string intent,
        string? successCriteria = null,
        NodeId? address = null,
        Capability? capability = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? slots = null)
    {
        var id = Guid.NewGuid().ToString("N");
        return new NodePacket
        {
            Id = id,
            CorrelationId = correlationId ?? id,
            Address = address,
            RequestedCapability = capability,
            State = NodeState.Created,
            Payload = new PacketPayload
            {
                Intent = intent,
                SuccessCriteria = successCriteria,
                Slots = slots is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(slots),
            },
        };
    }

    /// <summary>
    /// Advance to <paramref name="to"/>, appending a log entry. Throws <see cref="InvalidNodeTransitionException"/>
    /// if the transition is illegal — illegal transitions are bugs, not runtime conditions.
    /// Optionally renews the lease (pass <paramref name="leaseFor"/> while Working).
    /// </summary>
    public NodePacket Transition(
        NodeId node,
        NodeState to,
        string decision,
        Confidence? confidence = null,
        bool? success = null,
        string? error = null,
        IReadOnlyList<string>? artifacts = null,
        TimeSpan? leaseFor = null,
        DateTime? nowUtc = null)
    {
        if (!NodeStateMachine.CanTransition(State, to))
            throw new InvalidNodeTransitionException(State, to);

        var now = nowUtc ?? DateTime.UtcNow;
        var entry = new NodeLogEntry
        {
            Node = node,
            At = now,
            StateAfter = to,
            Decision = decision,
            Confidence = confidence ?? Confidence.Unassessed,
            Success = success,
            Error = error,
            Artifacts = artifacts ?? Array.Empty<string>(),
        };

        // Terminal states drop the lease; otherwise renew if asked, else keep the current lease.
        DateTime? lease = to.IsTerminal()
            ? null
            : leaseFor is { } span ? now.Add(span) : LeaseExpiresAt;

        return this with
        {
            State = to,
            Log = new List<NodeLogEntry>(Log) { entry },
            UpdatedAt = now,
            LeaseExpiresAt = lease,
        };
    }

    /// <summary>
    /// Park the packet awaiting an external human decision (§9): transition to
    /// <see cref="NodeState.AwaitingDependency"/> and CLEAR the lease, so it neither orphans nor holds a
    /// lease open while the human is away. It resumes via a normal transition once the decision lands.
    /// </summary>
    public NodePacket ParkAwaitingDependency(NodeId node, string decision, Confidence? confidence = null, DateTime? nowUtc = null)
    {
        if (!NodeStateMachine.CanTransition(State, NodeState.AwaitingDependency))
            throw new InvalidNodeTransitionException(State, NodeState.AwaitingDependency);

        var now = nowUtc ?? DateTime.UtcNow;
        var entry = new NodeLogEntry
        {
            Node = node,
            At = now,
            StateAfter = NodeState.AwaitingDependency,
            Decision = decision,
            Confidence = confidence ?? Confidence.Unassessed,
        };
        return this with
        {
            State = NodeState.AwaitingDependency,
            Log = new List<NodeLogEntry>(Log) { entry },
            UpdatedAt = now,
            LeaseExpiresAt = null,   // no lease held while parked
        };
    }

    /// <summary>Append a slot value (progressive disclosure) without changing state. No log entry.</summary>
    public NodePacket WithSlot(string key, string value, DateTime? nowUtc = null) =>
        this with { Payload = Payload.WithSlot(key, value), UpdatedAt = nowUtc ?? DateTime.UtcNow };

    /// <summary>Renew the working lease without a state change (heartbeat).</summary>
    public NodePacket RenewLease(TimeSpan leaseFor, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return this with { LeaseExpiresAt = now.Add(leaseFor), UpdatedAt = now };
    }

    /// <summary>True if the packet is active but its lease has lapsed (a watchdog target).</summary>
    public bool IsLeaseExpired(DateTime nowUtc) =>
        State.IsActive() && LeaseExpiresAt is { } exp && exp < nowUtc;

    /// <summary>Project a lightweight, pollable status (decision 1: state is polled, not request-driven).</summary>
    public NodePacketStatus ToStatus() => new()
    {
        Id = Id,
        CorrelationId = CorrelationId,
        State = State,
        IsTerminal = State.IsTerminal(),
        Address = Address,
        Confidence = LastEntry?.Confidence ?? Confidence.Unassessed,
        LastDecision = LastEntry?.Decision ?? "",
        LastError = LastEntry?.Error,
        LogEntryCount = Log.Count,
        LeaseExpiresAt = LeaseExpiresAt,
        UpdatedAt = UpdatedAt,
    };
}

/// <summary>A read-only, pollable snapshot of a packet's state. Cheap to serialize and return.</summary>
public sealed record NodePacketStatus
{
    public string Id { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public NodeState State { get; init; }
    public bool IsTerminal { get; init; }
    public NodeId? Address { get; init; }
    public Confidence Confidence { get; init; } = Confidence.Unassessed;
    public string LastDecision { get; init; } = "";
    public string? LastError { get; init; }
    public int LogEntryCount { get; init; }
    public DateTime? LeaseExpiresAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>Thrown when code attempts an illegal state transition — indicates a programming error.</summary>
public sealed class InvalidNodeTransitionException : Exception
{
    public InvalidNodeTransitionException(NodeState from, NodeState to)
        : base($"Illegal node state transition: {from} → {to}.") { }
}
