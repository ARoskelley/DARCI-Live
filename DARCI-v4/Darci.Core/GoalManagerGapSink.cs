using Darci.Goals;
using Darci.Nodes;
using Darci.Shared;
using Microsoft.Extensions.Logging;

namespace Darci.Core;

/// <summary>
/// Turns a deferred knowledge gap into a living-loop goal (the deferred path of gap-driven action).
/// Implements the Darci.Nodes <see cref="IGapGoalSink"/> seam so the gap handler stays goal-store
/// agnostic. Goals are clearly tagged as gap-sourced (Source=DarciInitiated, "[gap]" title prefix) and
/// carry the gap id + correlation id + originating intent in the description for traceability and so a
/// future ideation/theorizing node has the raw material to work from.
/// </summary>
public sealed class GoalManagerGapSink : IGapGoalSink
{
    private readonly IGoalManager _goals;
    private readonly ILogger<GoalManagerGapSink> _logger;

    public GoalManagerGapSink(IGoalManager goals, ILogger<GoalManagerGapSink> logger)
    {
        _goals = goals;
        _logger = logger;
    }

    public async Task<string?> CreateGoalForGapAsync(GapRecord gap, CancellationToken ct = default)
    {
        var creation = new GoalCreation
        {
            Title = $"[gap] {Truncate(gap.Question, 80)}",
            Description =
                "Auto-generated from a knowledge gap DARCI could not fill immediately.\n" +
                $"Question: {gap.Question}\n" +
                $"Missing: {gap.Missing}\n" +
                $"Originating intent: {gap.Intent}\n" +
                $"Source node: {gap.OriginNode}\n" +
                $"GapId: {gap.Id}\n" +
                $"CorrelationId: {gap.CorrelationId}",
            UserId = "DARCI",
            Type = GoalType.Task,
            Priority = GoalPriority.Low,        // non-blocking by definition — pick up when idle
            Source = GoalSource.DarciInitiated, // tags it as DARCI-generated, not user-requested
        };

        var goal = await _goals.CreateGoal(creation);
        _logger.LogInformation("Created gap-sourced goal {GoalId} for gap {GapId}.", goal.Id, gap.Id);
        return goal.Id.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
