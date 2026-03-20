using Darci.Shared;   // EngineeringGoalSpec lives here
using Microsoft.Extensions.Logging;

namespace Darci.Engineering;

/// <summary>
/// Coordinates engineering tool usage in a loop:
///   1. Reset tool with goal constraints
///   2. Get state → network selects action + params → execute on tool
///   3. Check reward, repeat until validation passes or max iterations
///   4. Return result for the behavioral layer
///
/// This is the engineering equivalent of the behavioral living loop,
/// running as a sub-loop within a single WorkOnGoal action.
/// </summary>
public class EngineeringOrchestrator
{
    private readonly ILogger<EngineeringOrchestrator> _logger;
    private readonly IEngineeringTool _workbench;
    private readonly IEngineeringNetwork _network;

    public int   MaxIterations  { get; set; } = 30;
    public float EarlyStopScore { get; set; } = 0.85f;

    public EngineeringOrchestrator(
        ILogger<EngineeringOrchestrator> logger,
        IEngineeringTool workbench,
        IEngineeringNetwork network)
    {
        _logger    = logger;
        _workbench = workbench;
        _network   = network;
    }

    /// <summary>
    /// Run a full engineering session for a goal spec.
    ///
    ///   1. Checks workbench service health
    ///   2. Resets the workbench with the goal's constraints/targets
    ///   3. Runs the neural-network loop (state → action → execute → repeat)
    ///   4. Validates the final result
    ///   5. Returns an EngineeringResult with score, validation, step count, etc.
    /// </summary>
    public async Task<EngineeringResult> RunAsync(
        EngineeringGoalSpec goal, CancellationToken ct = default)
    {
        _logger.LogInformation("Engineering orchestrator starting: {Description}", goal.Description);

        // Retry workbench health check with exponential back-off
        const int MaxRetries = 2;
        bool workbenchReady = false;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (await _workbench.IsHealthyAsync(ct))
            {
                workbenchReady = true;
                break;
            }
            if (attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s
                _logger.LogWarning(
                    "Workbench not ready, retrying in {Delay}s (attempt {A}/{Max})",
                    delay.TotalSeconds, attempt + 1, MaxRetries);
                await Task.Delay(delay, ct);
            }
        }

        if (!workbenchReady)
        {
            _logger.LogWarning("Geometry workbench service is not reachable after retries");
            return new EngineeringResult
            {
                Success      = false,
                ErrorMessage = "Geometry workbench unavailable after retries. " +
                               "Start it with: cd Darci.Engineering.Workbench && uvicorn main:app --port 8001. " +
                               "Research was completed and constraints were extracted — retry when workbench is running."
            };
        }

        // Reset workbench
        var resetResponse = await _workbench.ResetAsync(new WorkbenchResetRequest
        {
            ReferencePath = goal.ReferencePath,
            Constraints   = goal.Constraints,
            Targets       = goal.Targets,
        }, ct);

        var state      = resetResponse.State;
        var actionMask = resetResponse.ActionMask;
        float totalReward = 0f;
        int steps = 0;

        _logger.LogDebug("Workbench reset. State dim: {Dim}, starting loop", state.Length);

        // Engineering loop
        for (int i = 0; i < MaxIterations; i++)
        {
            if (ct.IsCancellationRequested) break;

            int    actionId;
            float[] parameters;

            if (_network.IsAvailable)
            {
                (actionId, parameters) = _network.SelectAction(state, actionMask);

                var logits     = _network.PredictLogits(state);
                var confidence = SoftmaxConfidence(logits, actionMask, actionId);
                _logger.LogDebug(
                    "Engineering action: {ActionId} (confidence: {Conf:P1})",
                    actionId, confidence);
            }
            else
            {
                // Exploration: random valid action, zero parameters
                var valid = Enumerable.Range(0, actionMask.Length).Where(j => actionMask[j]).ToList();
                actionId   = valid.Count > 0 ? valid[Random.Shared.Next(valid.Count)] : 0;
                parameters = new float[6];
                _logger.LogDebug("Engineering action (random exploration): {ActionId}", actionId);
            }

            var result = await _workbench.ExecuteAsync(actionId, parameters, ct);
            steps++;

            state      = result.State;
            actionMask = await _workbench.GetActionMaskAsync(ct);

            float stepReward = result.RewardComponents.Values.Sum();
            totalReward += stepReward;

            if (stepReward != 0)
                _logger.LogDebug("Step reward: {Reward:+0.00} (step {Step})", stepReward, steps);

            // Action 19 = finalize
            if (actionId == 19)
            {
                _logger.LogInformation("Network chose to finalize at step {Step}", steps);
                break;
            }

            // Early stop on high printability
            if (result.Metrics.TryGetValue("printability_score", out var printScore) &&
                printScore >= EarlyStopScore)
            {
                _logger.LogInformation(
                    "Early stop: printability {Score:F2} >= {Threshold:F2} at step {Step}",
                    printScore, EarlyStopScore, steps);
                break;
            }
        }

        var validation = await _workbench.ValidateAsync(ct);

        _logger.LogInformation(
            "Engineering complete. Steps: {Steps}, Score: {Score:F2}, Passed: {Passed}, Reward: {Reward:+0.0}",
            steps, validation.OverallScore, validation.Passed, totalReward);

        return new EngineeringResult
        {
            Success           = validation.Passed,
            FinalScore        = validation.OverallScore,
            ValidationPassed  = validation.Passed,
            StepsTaken        = steps,
            TotalReward       = totalReward,
            FinalValidation   = validation,
        };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static float SoftmaxConfidence(float[] logits, bool[] mask, int chosenAction)
    {
        var masked = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
            masked[i] = (i < mask.Length && mask[i]) ? logits[i] : float.NegativeInfinity;

        float max = masked.Max();
        var exp   = masked.Select(x => x == float.NegativeInfinity ? 0f : MathF.Exp(x - max)).ToArray();
        float sum = exp.Sum();
        return sum > 0 && chosenAction < exp.Length ? exp[chosenAction] / sum : 0f;
    }
}
