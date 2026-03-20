using Darci.Goals;
using Darci.Tools;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Darci.Core;

/// <summary>
/// Decomposes a goal into concrete ordered steps using an LLM.
/// Called fire-and-forget after goal creation so the goal pipeline
/// is populated before DARCI picks it up.
/// </summary>
public sealed class GoalDecomposer
{
    private readonly IGoalManager _goals;
    private readonly IToolkit _toolkit;
    private readonly ILogger<GoalDecomposer> _logger;

    public GoalDecomposer(
        IGoalManager goals,
        IToolkit toolkit,
        ILogger<GoalDecomposer> logger)
    {
        _goals = goals;
        _toolkit = toolkit;
        _logger = logger;
    }

    /// <summary>
    /// Decomposes the goal into 4-8 steps and persists them.
    /// Returns true if at least one step was added.
    /// Non-fatal — a decomposition failure leaves the default initial steps intact.
    /// </summary>
    public async Task<bool> DecomposeAsync(
        int goalId,
        string goalTitle,
        string? additionalContext,
        CancellationToken ct)
    {
        var prompt = $"""
You are a project planning assistant. Break this task into 4-8 ordered, specific steps.
Each step should be a concrete action with a clear completion criterion.
Respond ONLY with a JSON array of strings. No markdown, no numbering, no explanations.
Task: {goalTitle}
{(additionalContext != null ? $"Context: {additionalContext}" : "")}
""";

        try
        {
            var response = await _toolkit.Generate(prompt);
            response = response.Trim();

            // Strip markdown fences if present
            if (response.StartsWith("```"))
            {
                var firstNewline = response.IndexOf('\n');
                response = firstNewline >= 0 ? response[(firstNewline + 1)..] : response;
            }
            if (response.EndsWith("```"))
                response = response[..response.LastIndexOf("```")].TrimEnd();

            var steps = JsonSerializer.Deserialize<string[]>(response);
            if (steps is null || steps.Length == 0)
            {
                _logger.LogWarning("GoalDecomposer: empty step list for goal {Id}", goalId);
                return false;
            }

            foreach (var step in steps.Where(s => !string.IsNullOrWhiteSpace(s)))
                await _goals.AddStepAsync(goalId, step.Trim());

            _logger.LogInformation(
                "GoalDecomposer: decomposed goal {Id} into {Count} steps",
                goalId, steps.Length);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GoalDecomposer: decomposition failed for goal {Id}", goalId);
            return false;
        }
    }
}
