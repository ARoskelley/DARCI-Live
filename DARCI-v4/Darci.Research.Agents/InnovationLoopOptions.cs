#nullable enable

namespace Darci.Research.Agents;

/// <summary>
/// HARD budget backstop for the whole innovation loop (doc §7). Bounds compute/time/calls regardless of
/// progress — the knob that keeps innovation affordable on Tinman's single local-Ollama build.
/// </summary>
public sealed record InnovationBudget(
    int MaxCycles = 4,
    int MaxGenerativeCalls = 24,   // generation + screen + falsification calls (NOT the cheap embeddings)
    int MaxWallClockSeconds = 180);

/// <summary>Tuning for the diverse-candidate loop governor (Phase D). All configurable.</summary>
public sealed class InnovationLoopOptions
{
    public InnovationBudget Budget { get; init; } = new();

    /// <summary>Diverse candidates per cycle (Fable Q2: 3, not 5).</summary>
    public int CandidatesPerCycle { get; init; } = 3;

    /// <summary>Adaptive floor: N drops to this under budget pressure so the loop degrades gracefully.</summary>
    public int MinCandidates { get; init; } = 2;

    /// <summary>Top-K survivors of the screen that get the full falsification review.</summary>
    public int SurvivorsToFalsify { get; init; } = 2;

    /// <summary>Critic-quality at/above which the loop stops early with a winner.</summary>
    public double AcceptThreshold { get; init; } = 0.6;

    /// <summary>K: stop after this many consecutive cycles with no confidence/info improvement (plateau).</summary>
    public int PlateauCycles { get; init; } = 2;

    /// <summary>Min mean within-cycle pairwise distance; below it the diverse candidates collapsed → Unsolvable.</summary>
    public double NoveltyCollapseThreshold { get; init; } = 0.12;

    /// <summary>Cap on the tiny cross-cycle archive (QD-lite, best-per-cluster).</summary>
    public int ArchiveCap { get; init; } = 10;

    /// <summary>Embedding distance under which two candidates are the same archive cluster.</summary>
    public double ArchiveClusterThreshold { get; init; } = 0.15;

    /// <summary>Once this fraction of the generative-call budget is spent, drop N to <see cref="MinCandidates"/>.</summary>
    public double BudgetPressureFraction { get; init; } = 0.6;

    /// <summary>How many recent Retracted hypotheses to feed the generator as negatives.</summary>
    public int RetractedNegativesLimit { get; init; } = 8;
}
