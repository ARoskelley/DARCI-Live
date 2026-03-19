namespace Darci.Shared;

// ============================================================
// BRAIN ACTION - The 10 discrete actions the Decision Network
// can select. Matches ARCHITECTURE.md §4.1 exactly.
// These are integer IDs, not strings — the network outputs
// logits over these, not over English words.
// ============================================================

public enum BrainAction
{
    Rest               = 0,
    ReplyToMessage     = 1,
    Research           = 2,
    CreateGoal         = 3,
    WorkOnGoal         = 4,
    StoreMemory        = 5,
    RecallMemories     = 6,
    ConsolidateMemories = 7,
    NotifyUser         = 8,
    Think              = 9
}

// ============================================================
// EXPERIENCE - One DQN training tuple.
// Stores what happened: state → action → reward → next_state.
// Accumulated in ExperienceBuffer, sampled for training.
// ============================================================

public record Experience
{
    public long Id { get; init; }

    /// <summary>The 29-dimensional state vector before the action.</summary>
    public float[] State { get; init; } = Array.Empty<float>();

    /// <summary>The action taken (0–9, cast from BrainAction).</summary>
    public int Action { get; init; }

    /// <summary>The reward signal received after this action.</summary>
    public float Reward { get; init; }

    /// <summary>The 29-dimensional state vector after the action completed.</summary>
    public float[] NextState { get; init; } = Array.Empty<float>();

    /// <summary>True if this was the last action in an episode (e.g., shutdown).</summary>
    public bool IsTerminal { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

// ============================================================
// DECISION LOG - Records every decision for audit and training.
// Written by the instrumented Decision.Decide() in Core.
// ============================================================

public record DecisionLog
{
    public long Id { get; init; }

    /// <summary>The 29-dimensional state vector at decision time.</summary>
    public float[] StateVector { get; init; } = Array.Empty<float>();

    /// <summary>Action chosen (BrainAction int ID).</summary>
    public int ActionChosen { get; init; }

    /// <summary>
    /// True if the decision came from the neural network.
    /// False if it came from the v3 fallback priority ladder.
    /// </summary>
    public bool NetworkDecision { get; init; }

    /// <summary>Network confidence (softmax probability of chosen action). Null if fallback used.</summary>
    public float? Confidence { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
