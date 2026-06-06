#nullable enable

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Drives the autonomous coding agent loop: load context → plan → generate patch →
/// apply edits → build/test → retry or escalate → mark complete.
/// </summary>
public sealed class CodingAgentLoop : ICodingAgentLoop
{
    private const int MaxRetries = 3;
    private const int MaxStepPromptContextFiles = 5;
    private const int MaxPlanSteps = 20;

    private readonly ICodingWorkspaceStore _store;
    private readonly ICodingContextBuilder _contextBuilder;
    private readonly ISafeCommandRunner _runner;
    private readonly IModelRouter _router;
    private readonly IGitCheckpointService _checkpoints;
    private readonly IRoadblockDetector _roadblockDetector;
    private readonly PatchApplier _patchApplier;
    private readonly ILogger<CodingAgentLoop> _logger;

    private readonly ConcurrentDictionary<string, Task> _runningTasks = new();

    public CodingAgentLoop(
        ICodingWorkspaceStore store,
        ICodingContextBuilder contextBuilder,
        ISafeCommandRunner runner,
        IModelRouter router,
        IGitCheckpointService checkpoints,
        IRoadblockDetector roadblockDetector,
        PatchApplier patchApplier,
        ILogger<CodingAgentLoop> logger)
    {
        _store = store;
        _contextBuilder = contextBuilder;
        _runner = runner;
        _router = router;
        _checkpoints = checkpoints;
        _roadblockDetector = roadblockDetector;
        _patchApplier = patchApplier;
        _logger = logger;
    }

    public bool StartLoop(string taskId, RunCodingTaskRequest? options = null)
    {
        if (_runningTasks.ContainsKey(taskId)) return false;

        var task = Task.Run(async () =>
        {
            try
            {
                await RunLoopAsync(taskId, options, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in coding agent loop for task {TaskId}.", taskId);
            }
            finally
            {
                _runningTasks.TryRemove(taskId, out _);
            }
        });

        return _runningTasks.TryAdd(taskId, task);
    }

    public bool IsRunning(string taskId) => _runningTasks.ContainsKey(taskId);

    public async Task<CodingTaskStatusResponse?> GetStatusAsync(string taskId, CancellationToken ct = default)
    {
        var task = await _store.GetTaskAsync(taskId, ct);
        if (task is null) return null;

        var steps = DeserializeSteps(task.Plan);

        return new CodingTaskStatusResponse
        {
            TaskId = task.Id,
            Status = task.Status,
            CurrentStepIndex = task.CurrentStepIndex,
            TotalSteps = steps.Count,
            CurrentStepDescription = steps.Count > task.CurrentStepIndex
                ? steps[task.CurrentStepIndex].Description
                : null,
            LastStepResult = task.LastStepResult,
            RoadblockResearch = task.RoadblockResearch,
            IsRunning = IsRunning(taskId),
            UpdatedAt = task.UpdatedAt
        };
    }

    // ── Core loop ──────────────────────────────────────────────────────────

    private async Task RunLoopAsync(string taskId, RunCodingTaskRequest? options, CancellationToken ct)
    {
        var task = await _store.GetTaskAsync(taskId, ct);
        if (task is null)
        {
            _logger.LogWarning("CodingAgentLoop: task {TaskId} not found.", taskId);
            return;
        }

        var workspace = await _store.GetWorkspaceAsync(task.WorkspaceId, ct);
        if (workspace is null)
        {
            _logger.LogWarning("CodingAgentLoop: workspace {WorkspaceId} not found for task {TaskId}.",
                task.WorkspaceId, taskId);
            return;
        }

        _logger.LogInformation("CodingAgentLoop starting for task {TaskId} in workspace {WorkspaceId}.",
            taskId, workspace.Id);

        task = await UpdateTaskStatusAsync(task, "in_progress", ct);

        var steps = DeserializeSteps(task.Plan);
        if (steps.Count == 0)
        {
            _logger.LogWarning("Task {TaskId} has no plan steps.", taskId);
            task = await UpdateTaskStatusAsync(task, "failed", ct);
            return;
        }

        // Create a pre-run checkpoint.
        await _checkpoints.CreateCheckpointAsync(workspace.Id, taskId, $"Pre-run checkpoint for task: {task.Prompt}", ct);

        var buildCommand = PickBuildCommand(workspace.DetectedCommands);

        for (var stepIndex = task.CurrentStepIndex; stepIndex < Math.Min(steps.Count, MaxPlanSteps); stepIndex++)
        {
            var step = steps[stepIndex];
            _logger.LogInformation("Task {TaskId}: executing step {Step}/{Total}: {Desc}",
                taskId, stepIndex + 1, steps.Count, step.Description);

            // Update progress.
            steps[stepIndex] = step with { Status = "in_progress" };
            task = task with
            {
                CurrentStepIndex = stepIndex,
                Plan = JsonSerializer.Serialize(steps),
                UpdatedAt = DateTime.UtcNow
            };
            await _store.UpdateTaskAsync(task, ct);

            // Rebuild context for this step.
            var context = await _contextBuilder.BuildAsync(workspace.Id, step.Description + " " + task.Prompt, 6, ct);

            var stepResult = "";
            var stepSuccess = false;

            // Attempt the step with retries.
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                var prompt = BuildStepPrompt(task, workspace, step, stepIndex, steps.Count, context, stepResult, attempt);
                var llmResponse = await _router.GenerateAsync(prompt, ModelTaskType.Coding, ct);

                if (string.IsNullOrWhiteSpace(llmResponse))
                {
                    stepResult = "[LLM returned empty response]";
                    continue;
                }

                // Apply any file edits from the response.
                var patchResults = await _patchApplier.ApplyAsync(workspace.RootPath, llmResponse, ct);
                var appliedCount = patchResults.Count(r => r.Success);
                var failedPatches = patchResults.Where(r => !r.Success).Select(r => $"{r.RelativePath}: {r.Error}").ToList();

                _logger.LogInformation("Task {TaskId} step {Step} attempt {Attempt}: applied {Applied}/{Total} patches.",
                    taskId, stepIndex + 1, attempt + 1, appliedCount, patchResults.Count);

                // Run build/test command.
                if (buildCommand is not null)
                {
                    var (cmd, args) = ParseCommand(buildCommand);
                    var cmdRun = await _runner.RunForTaskAsync(workspace.Id, taskId,
                        new CodingCommandRequest(cmd, args, TimeoutSeconds: 120), ct);

                    stepResult = FormatCommandResult(cmdRun);

                    if (cmdRun.ExitCode == 0)
                    {
                        stepSuccess = true;
                        break;
                    }

                    // Check for roadblock after enough failures.
                    if (attempt == MaxRetries - 1)
                    {
                        var research = await _roadblockDetector.CheckAndResearchAsync(
                            workspace.Id, taskId, buildCommand, cmdRun.StderrTail, ct);

                        if (!string.IsNullOrWhiteSpace(research))
                        {
                            task = task with { RoadblockResearch = research, UpdatedAt = DateTime.UtcNow };
                            await _store.UpdateTaskAsync(task, ct);
                            steps[stepIndex] = step with { Status = "roadblocked", Result = stepResult };
                            task = task with { Plan = JsonSerializer.Serialize(steps) };
                            await _store.UpdateTaskAsync(task, ct);
                            _logger.LogWarning("Task {TaskId} step {Step} roadblocked.", taskId, stepIndex + 1);
                            // Continue to next step — research is saved for review.
                        }
                    }
                }
                else
                {
                    // No build command — treat LLM completing as success if patches applied.
                    stepSuccess = appliedCount > 0 || patchResults.Count == 0;
                    stepResult = appliedCount > 0
                        ? $"Applied {appliedCount} file edit(s)."
                        : string.IsNullOrWhiteSpace(llmResponse) ? "[No edits]" : "[Analysis complete — no file edits]";
                    if (failedPatches.Count > 0)
                    {
                        stepResult += $"\nPatch errors: {string.Join("; ", failedPatches)}";
                    }

                    if (stepSuccess) break;
                }
            }

            // Update step status.
            steps[stepIndex] = steps[stepIndex] with
            {
                Status = stepSuccess ? "completed" : steps[stepIndex].Status == "roadblocked" ? "roadblocked" : "failed",
                Result = stepResult
            };
            task = task with
            {
                Plan = JsonSerializer.Serialize(steps),
                LastStepResult = stepResult,
                CurrentStepIndex = stepIndex,
                UpdatedAt = DateTime.UtcNow
            };
            await _store.UpdateTaskAsync(task, ct);
        }

        // Determine final status.
        var allComplete = steps.All(s => s.Status is "completed" or "roadblocked");
        var anyFailed = steps.Any(s => s.Status == "failed");
        var anyRoadblocked = steps.Any(s => s.Status == "roadblocked");

        var finalStatus = allComplete && !anyFailed ? "completed" : anyRoadblocked ? "blocked" : "failed";

        // Run success verification if provided.
        if (finalStatus == "completed" && options?.SuccessVerificationCommand is not null)
        {
            var (vcmd, vargs) = ParseCommand(options.SuccessVerificationCommand);
            if (options.SuccessVerificationArguments is { Length: > 0 })
                vargs = options.SuccessVerificationArguments;
            var verifyRun = await _runner.RunForTaskAsync(workspace.Id, taskId,
                new CodingCommandRequest(vcmd, vargs, TimeoutSeconds: 120), ct);
            if (verifyRun.ExitCode != 0)
            {
                finalStatus = "verification-failed";
                _logger.LogWarning("Task {TaskId}: success verification command failed.", taskId);
            }
        }

        task = task with
        {
            Status = finalStatus,
            CurrentStepIndex = steps.Count - 1,
            UpdatedAt = DateTime.UtcNow
        };
        await _store.UpdateTaskAsync(task, ct);

        _logger.LogInformation("CodingAgentLoop finished task {TaskId} with status {Status}.", taskId, finalStatus);
    }

    // ── Prompt building ────────────────────────────────────────────────────

    private static string BuildStepPrompt(
        CodingTaskRecord task,
        CodingWorkspace workspace,
        CodingPlanStep step,
        int stepIndex,
        int totalSteps,
        CodingContextPackage context,
        string previousCommandOutput,
        int attempt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert software engineer. Your job is to implement a specific step in a coding task.");
        sb.AppendLine();
        sb.AppendLine($"TASK: {task.Prompt}");
        sb.AppendLine($"SUCCESS CRITERIA: {task.SuccessCriteria}");
        sb.AppendLine($"WORKSPACE: {workspace.Name} ({string.Join(", ", workspace.DetectedLanguages)})");
        sb.AppendLine();
        sb.AppendLine($"CURRENT STEP ({stepIndex + 1}/{totalSteps}): {step.Description}");

        if (attempt > 0 && !string.IsNullOrWhiteSpace(previousCommandOutput))
        {
            sb.AppendLine();
            sb.AppendLine($"PREVIOUS ATTEMPT FAILED (attempt {attempt}/{MaxRetries}):");
            sb.AppendLine(previousCommandOutput.Length > 800 ? previousCommandOutput[^800..] : previousCommandOutput);
            sb.AppendLine("Please fix the error above.");
        }

        if (!string.IsNullOrWhiteSpace(task.RoadblockResearch))
        {
            sb.AppendLine();
            sb.AppendLine("RESEARCH (from roadblock investigation):");
            sb.AppendLine(task.RoadblockResearch.Length > 600 ? task.RoadblockResearch[..600] : task.RoadblockResearch);
        }

        if (context.KgHits.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RELATED CODE SYMBOLS:");
            foreach (var hit in context.KgHits.Take(4))
                sb.AppendLine($"- {hit.Name} ({hit.EntityType}): {hit.Description}");
        }

        if (context.Files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RELEVANT FILES:");
            foreach (var f in context.Files.Take(MaxStepPromptContextFiles))
            {
                sb.AppendLine($"### {f.RelativePath}");
                sb.AppendLine("```");
                var preview = f.Preview.Length > 1500 ? f.Preview[..1500] + "\n[truncated]" : f.Preview;
                sb.AppendLine(preview);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("- Produce ONLY the file edits needed for this step. Do not include explanations outside code blocks.");
        sb.AppendLine("- For each file you need to create or modify, use this EXACT format:");
        sb.AppendLine();
        sb.AppendLine("### FILE: relative/path/to/file.cs");
        sb.AppendLine("```csharp");
        sb.AppendLine("// full file content here");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("- Use the file's actual extension in the code fence language specifier.");
        sb.AppendLine("- If no file edits are needed for this step, write only: NO_EDITS_NEEDED");
        sb.AppendLine("- Do NOT write anything outside the FILE blocks except NO_EDITS_NEEDED.");

        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string? PickBuildCommand(string[] commands)
    {
        // Prefer build commands over test commands for iteration speed.
        return commands.FirstOrDefault(c => c.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("cargo build", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("go build", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("npm run build", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault();
    }

    private static (string Command, string[] Args) ParseCommand(string fullCommand)
    {
        var parts = fullCommand.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts[1..]);
    }

    private static string FormatCommandResult(CodingCommandRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Exit code: {run.ExitCode?.ToString() ?? "(null)"}{(run.TimedOut ? " [timed out]" : "")}");
        if (!string.IsNullOrWhiteSpace(run.StdoutTail))
            sb.AppendLine($"stdout:\n{run.StdoutTail[^Math.Min(600, run.StdoutTail.Length)..]}");
        if (!string.IsNullOrWhiteSpace(run.StderrTail))
            sb.AppendLine($"stderr:\n{run.StderrTail[^Math.Min(600, run.StderrTail.Length)..]}");
        return sb.ToString().Trim();
    }

    private static List<CodingPlanStep> DeserializeSteps(string planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<CodingPlanStep>>(planJson) ?? new();
        }
        catch
        {
            // Legacy free-text plan — wrap as a single step.
            return new List<CodingPlanStep>
            {
                new() { StepNumber = 1, Description = planJson.Trim(), Status = "pending" }
            };
        }
    }

    private async Task<CodingTaskRecord> UpdateTaskStatusAsync(CodingTaskRecord task, string status, CancellationToken ct)
    {
        var updated = task with { Status = status, UpdatedAt = DateTime.UtcNow };
        await _store.UpdateTaskAsync(updated, ct);
        return updated;
    }
}
