#nullable enable

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Drives the autonomous coding agent loop: load context → plan → generate patch →
/// apply edits → build → behavioral verification → retry or escalate → mark complete.
/// </summary>
public sealed class CodingAgentLoop : ICodingAgentLoop
{
    private const int MaxRetries = 3;
    // Kept small: a 7B model treats every full file it is shown as a candidate edit target and
    // will regenerate/corrupt reference files. Fewer reference files = less corruption surface
    // and faster generation on contended local hardware.
    private const int MaxStepPromptContextFiles = 3;
    private const int MaxPlanSteps = 20;
    private const int EarlyEscalationAttempt = 1;   // trigger roadblock research at 2nd attempt
    private const double LowConfidenceThreshold = 0.4;
    private const long MaxFullFileBytes = 51_200;    // 50 KB: send complete content below this
    private const int MaxFullFileChars = 40_000;     // truncate above this

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
                // Guarantee a terminal status so the task can never be orphaned at "in_progress".
                await MarkTaskAbortedAsync(taskId, ex);
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
            VerificationResult = task.VerificationResult,
            ConfidenceScore = task.ConfidenceScore,
            ConfidenceNote = task.ConfidenceNote,
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
        var testCommand = PickTestCommand(workspace.DetectedCommands);

        // Track total file writes across the whole task so a no-op run can never
        // be reported as "completed" just because an unchanged tree still builds.
        var totalFilesWritten = 0;

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
                var prompt = await BuildStepPromptAsync(
                    task, workspace, step, stepIndex, steps.Count,
                    context, stepResult, attempt, testCommand is not null, ct);

                var llmResponse = await _router.GenerateAsync(prompt, ModelTaskType.Coding, ct);

                if (string.IsNullOrWhiteSpace(llmResponse))
                {
                    stepResult = "[LLM returned empty response]";
                    continue;
                }

                // Parse and store confidence annotation.
                var (confidence, confidenceNote) = ParseConfidence(llmResponse);
                if (confidence >= 0)
                {
                    task = task with
                    {
                        ConfidenceScore = confidence,
                        ConfidenceNote = confidenceNote,
                        UpdatedAt = DateTime.UtcNow
                    };
                    await _store.UpdateTaskAsync(task, ct);
                }

                // Strip meta-annotations, then split VERIFY blocks from FILE blocks.
                var strippedResponse = StripMetaAnnotations(llmResponse);
                var (codeResponse, verifyResponse) = SplitVerifyBlocks(strippedResponse);

                // Apply code file edits.
                var patchResults = await _patchApplier.ApplyAsync(workspace.RootPath, codeResponse, ct);
                var appliedCount = patchResults.Count(r => r.Success);
                var failedPatches = patchResults.Where(r => !r.Success).Select(r => $"{r.RelativePath}: {r.Error}").ToList();

                _logger.LogInformation("Task {TaskId} step {Step} attempt {Attempt}: applied {Applied}/{Total} patches.",
                    taskId, stepIndex + 1, attempt + 1, appliedCount, patchResults.Count);

                totalFilesWritten += appliedCount;

                if (buildCommand is not null)
                {
                    var (cmd, args) = ParseCommand(buildCommand);
                    var cmdRun = await _runner.RunForTaskAsync(workspace.Id, taskId,
                        new CodingCommandRequest(cmd, args, TimeoutSeconds: 120), ct);

                    stepResult = FormatCommandResult(cmdRun);

                    if (cmdRun.ExitCode != 0)
                    {
                        // Build failed — check roadblock early (at 2nd attempt onwards).
                        if (attempt >= EarlyEscalationAttempt)
                        {
                            await TryEscalateRoadblockAsync(task, workspace.Id, taskId, buildCommand,
                                cmdRun.StderrTail, ct,
                                updated => task = updated);
                        }

                        if (attempt == MaxRetries - 1)
                        {
                            // Final attempt — mark roadblocked.
                            steps[stepIndex] = step with { Status = "roadblocked", Result = stepResult };
                            task = task with { Plan = JsonSerializer.Serialize(steps), UpdatedAt = DateTime.UtcNow };
                            await _store.UpdateTaskAsync(task, ct);
                            _logger.LogWarning("Task {TaskId} step {Step} roadblocked after build failures.", taskId, stepIndex + 1);
                        }

                        if (failedPatches.Count > 0)
                            stepResult += $"\nPatch errors: {string.Join("; ", failedPatches)}";
                        continue;
                    }

                    // Build succeeded — run behavioral verification if we have verify blocks.
                    if (testCommand is not null && !string.IsNullOrWhiteSpace(verifyResponse))
                    {
                        var verifyApply = await _patchApplier.ApplyAsync(workspace.RootPath, verifyResponse, ct);
                        var verifyApplied = verifyApply.Count(r => r.Success);
                        totalFilesWritten += verifyApplied;
                        _logger.LogInformation("Task {TaskId} step {Step}: applied {V} verify file(s).",
                            taskId, stepIndex + 1, verifyApplied);

                        var (tcmd, targs) = ParseCommand(testCommand);
                        var testRun = await _runner.RunForTaskAsync(workspace.Id, taskId,
                            new CodingCommandRequest(tcmd, targs, TimeoutSeconds: 180), ct);

                        var verifyOutput = FormatCommandResult(testRun);
                        task = task with { VerificationResult = verifyOutput, UpdatedAt = DateTime.UtcNow };
                        await _store.UpdateTaskAsync(task, ct);

                        if (testRun.ExitCode != 0)
                        {
                            // Behavioral failure — treat same as build failure.
                            stepResult = $"BUILD PASSED but BEHAVIORAL VERIFICATION FAILED:\n{verifyOutput}";
                            _logger.LogWarning("Task {TaskId} step {Step} attempt {Attempt}: behavioral verification failed.",
                                taskId, stepIndex + 1, attempt + 1);

                            // Proactive low-confidence research (before spending more retries).
                            if (confidence >= 0 && confidence < LowConfidenceThreshold
                                && !string.IsNullOrWhiteSpace(confidenceNote)
                                && !string.Equals(confidenceNote, "nothing", StringComparison.OrdinalIgnoreCase))
                            {
                                await TryProactiveResearchAsync(task, taskId, confidenceNote, ct,
                                    updated => task = updated);
                            }

                            if (attempt >= EarlyEscalationAttempt)
                            {
                                await TryEscalateRoadblockAsync(task, workspace.Id, taskId, testCommand,
                                    testRun.StderrTail, ct,
                                    updated => task = updated);
                            }

                            if (attempt == MaxRetries - 1)
                            {
                                steps[stepIndex] = step with { Status = "roadblocked", Result = stepResult };
                                task = task with { Plan = JsonSerializer.Serialize(steps), UpdatedAt = DateTime.UtcNow };
                                await _store.UpdateTaskAsync(task, ct);
                                _logger.LogWarning("Task {TaskId} step {Step} roadblocked after behavioral failures.", taskId, stepIndex + 1);
                            }

                            continue;
                        }
                    }
                    else if (confidence >= 0 && confidence < LowConfidenceThreshold
                             && !string.IsNullOrWhiteSpace(confidenceNote)
                             && !string.Equals(confidenceNote, "nothing", StringComparison.OrdinalIgnoreCase))
                    {
                        // Build succeeded but model is uncertain and no test was emitted — do proactive research.
                        await TryProactiveResearchAsync(task, taskId, confidenceNote, ct,
                            updated => task = updated);
                    }

                    stepSuccess = true;
                    break;
                }
                else
                {
                    // No build command — treat LLM completing as success if patches applied.
                    stepSuccess = appliedCount > 0 || patchResults.Count == 0;
                    stepResult = appliedCount > 0
                        ? $"Applied {appliedCount} file edit(s)."
                        : string.IsNullOrWhiteSpace(llmResponse) ? "[No edits]" : "[Analysis complete — no file edits]";
                    if (failedPatches.Count > 0)
                        stepResult += $"\nPatch errors: {string.Join("; ", failedPatches)}";
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

        // GUARD 1 — false-success guard. A code task that wrote zero files cannot be
        // "completed" just because an unchanged tree still builds with exit 0. This is the
        // core blindness fix: build-exit-0 on a no-op run must NOT read as success.
        if (finalStatus == "completed" && totalFilesWritten == 0)
        {
            finalStatus = "no-op";
            var msg = "Task produced zero file changes across all steps (model returned NO_EDITS_NEEDED " +
                      "or unparseable output). An unchanged tree builds successfully, so this is NOT a real success.";
            task = task with { LastStepResult = msg, UpdatedAt = DateTime.UtcNow };
            await _store.UpdateTaskAsync(task, ct);
            _logger.LogWarning("Task {TaskId}: {Msg}", taskId, msg);
        }

        // GUARD 2 — mandatory end-of-task behavioral verification gate. If the workspace has a
        // test command and the task wrote files, run the tests as a final gate regardless of
        // whether the model volunteered any ### VERIFY: blocks. Green build is not enough.
        if (finalStatus == "completed" && testCommand is not null && totalFilesWritten > 0)
        {
            var (tcmd, targs) = ParseCommand(testCommand);
            var finalTest = await _runner.RunForTaskAsync(workspace.Id, taskId,
                new CodingCommandRequest(tcmd, targs, TimeoutSeconds: 180), ct);
            var finalTestOutput = FormatCommandResult(finalTest);
            task = task with { VerificationResult = finalTestOutput, UpdatedAt = DateTime.UtcNow };
            await _store.UpdateTaskAsync(task, ct);

            if (finalTest.ExitCode != 0)
            {
                finalStatus = "verification-failed";
                _logger.LogWarning("Task {TaskId}: final behavioral verification gate failed.", taskId);
            }
        }

        // Run caller-supplied success verification if provided (additional gate).
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

        _logger.LogInformation("CodingAgentLoop finished task {TaskId} with status {Status} ({Files} file write(s)).",
            taskId, finalStatus, totalFilesWritten);
    }

    // ── Escalation helpers ─────────────────────────────────────────────────

    private async Task TryEscalateRoadblockAsync(
        CodingTaskRecord task,
        string workspaceId,
        string taskId,
        string command,
        string stderrSnippet,
        CancellationToken ct,
        Action<CodingTaskRecord> updateTask)
    {
        try
        {
            var research = await _roadblockDetector.CheckAndResearchAsync(
                workspaceId, taskId, command, stderrSnippet, ct);

            if (!string.IsNullOrWhiteSpace(research) && research != task.RoadblockResearch)
            {
                var updated = task with { RoadblockResearch = research, UpdatedAt = DateTime.UtcNow };
                await _store.UpdateTaskAsync(updated, ct);
                updateTask(updated);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Roadblock escalation check failed (non-fatal).");
        }
    }

    private async Task TryProactiveResearchAsync(
        CodingTaskRecord task,
        string taskId,
        string confidenceNote,
        CancellationToken ct,
        Action<CodingTaskRecord> updateTask)
    {
        try
        {
            _logger.LogInformation("Task {TaskId}: low confidence ({Score:F2}), running proactive research on: {Note}",
                taskId, task.ConfidenceScore, confidenceNote.Length > 80 ? confidenceNote[..80] : confidenceNote);

            var question = $"[Task: {task.Prompt}] After implementing, the model reports uncertainty about: {confidenceNote}. " +
                           "Provide concrete guidance to resolve this uncertainty correctly.";

            var research = await _roadblockDetector.ResearchTopicAsync(question, ct);
            if (!string.IsNullOrWhiteSpace(research))
            {
                var updated = task with { RoadblockResearch = research, UpdatedAt = DateTime.UtcNow };
                await _store.UpdateTaskAsync(updated, ct);
                updateTask(updated);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Proactive research failed (non-fatal).");
        }
    }

    // ── Prompt building ────────────────────────────────────────────────────

    private async Task<string> BuildStepPromptAsync(
        CodingTaskRecord task,
        CodingWorkspace workspace,
        CodingPlanStep step,
        int stepIndex,
        int totalSteps,
        CodingContextPackage context,
        string previousCommandOutput,
        int attempt,
        bool hasTestCommand,
        CancellationToken ct)
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
            var errWindow = previousCommandOutput.Length > 1200 ? previousCommandOutput[^1200..] : previousCommandOutput;
            sb.AppendLine(errWindow);
            sb.AppendLine("Please fix the error above.");
        }

        if (!string.IsNullOrWhiteSpace(task.RoadblockResearch))
        {
            sb.AppendLine();
            sb.AppendLine("RESEARCH (from roadblock investigation or proactive lookup):");
            var researchWindow = task.RoadblockResearch.Length > 800 ? task.RoadblockResearch[..800] : task.RoadblockResearch;
            sb.AppendLine(researchWindow);
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
            sb.AppendLine("REFERENCE FILES — READ-ONLY CONTEXT. These show existing types and conventions so");
            sb.AppendLine("your new code compiles against them. You MUST NOT modify, regenerate, reformat, or emit");
            sb.AppendLine("a FILE block for any of these paths. They are background only — not edit targets.");
            foreach (var f in context.Files.Take(MaxStepPromptContextFiles))
            {
                sb.AppendLine();
                sb.AppendLine($"----- REFERENCE (DO NOT EDIT): {f.RelativePath} -----");
                sb.AppendLine("```");
                var content = await ReadFullFileAsync(workspace.RootPath, f.RelativePath, ct);
                sb.AppendLine(content);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("INSTRUCTIONS:");
        sb.AppendLine("- Create or modify ONLY the file path(s) explicitly named in the TASK above. Never emit a");
        sb.AppendLine("  FILE block for a REFERENCE file or any path the task did not ask you to change.");
        sb.AppendLine("- Use the exact class names, method signatures, and file paths the TASK specifies.");
        sb.AppendLine("- Produce ONLY the file edits needed for this step. Do not include explanations outside code blocks.");
        sb.AppendLine("- For each file you need to create or modify, use this EXACT format:");
        sb.AppendLine();
        sb.AppendLine("### FILE: relative/path/to/file.cs");
        sb.AppendLine("```csharp");
        sb.AppendLine("// complete file content here");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("- Use the file's actual extension in the code fence language specifier.");
        sb.AppendLine("- Write COMPLETE file content — never truncate or leave TODO placeholders.");
        sb.AppendLine("- This step is part of a larger task. If creating or modifying a code file is implied");
        sb.AppendLine("  by the overall TASK, you MUST emit the complete file now — even if this specific step");
        sb.AppendLine("  sounds preparatory (e.g. 'create the file', 'add usings'). Produce the real, working file.");
        sb.AppendLine("- NO_EDITS_NEEDED is ONLY for purely analytical steps (e.g. 'review the code'). Do NOT use it");
        sb.AppendLine("  to defer code that the task requires — deferring leaves the task incomplete and will fail.");
        sb.AppendLine();
        sb.AppendLine("CONSTRAINTS:");
        sb.AppendLine("- Prefer double over float or int for numeric results unless integer arithmetic is explicitly required.");
        sb.AppendLine("- Never insert casts that silently truncate precision (e.g., (int)(2.5 * 4.0) would lose the decimal).");
        sb.AppendLine("- One class per file; match existing file naming and namespace conventions exactly.");
        sb.AppendLine("- Do NOT write anything outside FILE and VERIFY blocks except the CONFIDENCE metadata below.");

        if (hasTestCommand)
        {
            sb.AppendLine();
            sb.AppendLine("BEHAVIORAL VERIFICATION:");
            sb.AppendLine("After your FILE blocks, emit test file(s) that exercise the actual runtime behavior:");
            sb.AppendLine();
            sb.AppendLine("### VERIFY: Tests/BehaviorCheck.cs");
            sb.AppendLine("```csharp");
            sb.AppendLine("// xUnit or NUnit test — assert observable behavior, not just that the code compiles.");
            sb.AppendLine("// If no test project exists, also emit a minimal .csproj for it as a separate VERIFY block.");
            sb.AppendLine("// Focus on the most likely behavioral failure mode (wrong numeric type, off-by-one, null, etc.).");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("These VERIFY files will be written to the workspace and run with the test command.");
        }

        sb.AppendLine();
        sb.AppendLine("METADATA (required — place after all FILE and VERIFY blocks, on separate lines):");
        sb.AppendLine("CONFIDENCE: <0.0–1.0>");
        sb.AppendLine("UNSURE_ABOUT: <the one thing you are most uncertain about, or 'nothing'>");

        return sb.ToString();
    }

    // ── Response parsing ───────────────────────────────────────────────────

    private static (double Confidence, string Note) ParseConfidence(string response)
    {
        var confidence = -1.0;
        var note = "";

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("CONFIDENCE:", StringComparison.OrdinalIgnoreCase))
            {
                var val = trimmed["CONFIDENCE:".Length..].Trim();
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    confidence = Math.Clamp(d, 0.0, 1.0);
            }
            else if (trimmed.StartsWith("UNSURE_ABOUT:", StringComparison.OrdinalIgnoreCase))
            {
                note = trimmed["UNSURE_ABOUT:".Length..].Trim();
            }
        }

        return (confidence, note);
    }

    private static string StripMetaAnnotations(string response)
    {
        var lines = response.Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("CONFIDENCE:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("UNSURE_ABOUT:", StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(line);
        }
        return string.Join('\n', result);
    }

    /// <summary>
    /// Separates <c>### VERIFY: path</c> blocks from <c>### FILE: path</c> blocks.
    /// VERIFY blocks are converted to FILE-format so PatchApplier can write them.
    /// Returns (codeOnlyResponse, verifyOnlyResponse).
    /// </summary>
    private static (string CodeResponse, string VerifyResponse) SplitVerifyBlocks(string response)
    {
        var codeLines = new List<string>();
        var verifyLines = new List<string>();
        var inVerify = false;
        const string verifyPrefix = "### VERIFY:";
        const string filePrefix = "### FILE:";

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(verifyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = trimmed[verifyPrefix.Length..].Trim();
                verifyLines.Add($"### FILE: {path}");
                inVerify = true;
            }
            else if (trimmed.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
            {
                codeLines.Add(line);
                inVerify = false;
            }
            else if (inVerify)
            {
                verifyLines.Add(line);
            }
            else
            {
                codeLines.Add(line);
            }
        }

        return (string.Join('\n', codeLines), string.Join('\n', verifyLines));
    }

    // ── File reading ───────────────────────────────────────────────────────

    private static async Task<string> ReadFullFileAsync(string rootPath, string relativePath, CancellationToken ct)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            var normalizedRoot = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return "[File read refused: path resolves outside workspace root.]";

            if (!File.Exists(fullPath))
                return "[File not found on disk.]";

            var info = new FileInfo(fullPath);
            if (info.Length > MaxFullFileBytes)
            {
                // Large file: send first half + last half with a gap marker.
                var text = await File.ReadAllTextAsync(fullPath, ct);
                if (text.Length <= MaxFullFileChars) return text;
                var half = MaxFullFileChars / 2;
                return text[..half]
                    + $"\n\n[... {text.Length - MaxFullFileChars:N0} chars omitted — file exceeds context budget ...]\n\n"
                    + text[^half..];
            }

            return await File.ReadAllTextAsync(fullPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[Preview unavailable: {ex.Message}]";
        }
    }

    // ── Command helpers ────────────────────────────────────────────────────

    private static string? PickBuildCommand(string[] commands)
    {
        // Prefer build commands over test commands for iteration speed.
        return commands.FirstOrDefault(c => c.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("cargo build", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("go build", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("npm run build", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault();
    }

    private static string? PickTestCommand(string[] commands)
    {
        return commands.FirstOrDefault(c => c.StartsWith("dotnet test", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("pytest", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("py -m pytest", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("python -m pytest", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("npm test", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("cargo test", StringComparison.OrdinalIgnoreCase))
            ?? commands.FirstOrDefault(c => c.StartsWith("go test", StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Best-effort terminal status write when the loop aborts with an unhandled exception, so a task
    /// is never left orphaned at "in_progress". Only overwrites non-terminal statuses.
    /// </summary>
    private async Task MarkTaskAbortedAsync(string taskId, Exception ex)
    {
        try
        {
            var task = await _store.GetTaskAsync(taskId);
            if (task is null || task.Status is not ("in_progress" or "planned" or "planning")) return;

            await _store.UpdateTaskAsync(task with
            {
                Status = "failed",
                LastStepResult = $"Loop aborted: {ex.GetType().Name}: {ex.Message}",
                UpdatedAt = DateTime.UtcNow
            });
            _logger.LogWarning("Task {TaskId} marked failed after unhandled loop exception.", taskId);
        }
        catch (Exception inner)
        {
            _logger.LogWarning(inner, "Could not mark task {TaskId} failed after abort.", taskId);
        }
    }
}
