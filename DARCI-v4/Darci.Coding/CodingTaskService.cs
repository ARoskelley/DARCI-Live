#nullable enable

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Darci.Coding;

public sealed class CodingTaskService : ICodingTaskService
{
    private readonly ICodingWorkspaceStore _store;
    private readonly ICodingContextBuilder _contextBuilder;
    private readonly IModelRouter _router;

    public CodingTaskService(
        ICodingWorkspaceStore store,
        ICodingContextBuilder contextBuilder,
        IModelRouter router)
    {
        _store = store;
        _contextBuilder = contextBuilder;
        _router = router;
    }

    public async Task<CodingTaskRecord> CreateTaskAsync(CreateCodingTaskRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new ArgumentException("WorkspaceId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var context = await _contextBuilder.BuildAsync(request.WorkspaceId, request.Prompt, limit: 10, ct);
        var workspace = await _store.GetWorkspaceAsync(request.WorkspaceId, ct);

        var now = DateTime.UtcNow;

        var (planJson, generatedBy, model) = await BuildPlanAsync(request.Prompt, context, workspace, ct);

        var task = new CodingTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = request.WorkspaceId,
            Prompt = request.Prompt.Trim(),
            SuccessCriteria = request.SuccessCriteria?.Trim() ?? "Build/test command succeeds or requested behaviour is verified.",
            CreatedBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? "Tinman" : request.CreatedBy.Trim(),
            Status = "planned",
            Plan = planJson,
            LastContextPackageJson = JsonSerializer.Serialize(context),
            PlanGeneratedBy = generatedBy,
            PlanModel = model,
            CurrentStepIndex = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _store.AddTaskAsync(task, ct);
        return task;
    }

    private async Task<(string PlanJson, string GeneratedBy, string Model)> BuildPlanAsync(
        string prompt,
        CodingContextPackage context,
        CodingWorkspace? workspace,
        CancellationToken ct)
    {
        // Try LLM-driven plan first.
        try
        {
            var llmPlan = await GeneratePlanWithLlmAsync(prompt, context, workspace, ct);
            if (llmPlan is { Count: > 0 })
            {
                return (JsonSerializer.Serialize(llmPlan), "llm", GetModelName());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to template.
            _ = ex;
        }

        return (JsonSerializer.Serialize(BuildTemplatePlan(context)), "template", "");
    }

    private async Task<List<CodingPlanStep>?> GeneratePlanWithLlmAsync(
        string prompt,
        CodingContextPackage context,
        CodingWorkspace? workspace,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a software engineering assistant. Create a concise step-by-step plan for the following coding task.");
        sb.AppendLine();
        sb.AppendLine($"TASK: {prompt}");
        sb.AppendLine();

        if (workspace is not null)
        {
            sb.AppendLine($"WORKSPACE: {workspace.Name}");
            if (workspace.DetectedLanguages.Length > 0)
                sb.AppendLine($"LANGUAGES: {string.Join(", ", workspace.DetectedLanguages)}");
            if (workspace.DetectedCommands.Length > 0)
                sb.AppendLine($"AVAILABLE COMMANDS: {string.Join(", ", workspace.DetectedCommands.Take(5))}");
        }

        if (context.KgHits.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RELATED CODE SYMBOLS (from knowledge graph):");
            foreach (var hit in context.KgHits.Take(5))
                sb.AppendLine($"- {hit.Name} ({hit.EntityType}): {hit.Description}");
        }

        if (context.Files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("MOST RELEVANT FILES:");
            foreach (var f in context.Files.Take(6))
                sb.AppendLine($"- {f.RelativePath} ({f.Kind})");
        }

        sb.AppendLine();
        sb.AppendLine("Produce a SHORT plan of 2-4 COARSE steps. Critical rules:");
        sb.AppendLine("- Each step must produce one or more COMPLETE files, not fragments.");
        sb.AppendLine("- Do NOT split a single file into multiple steps. 'Create the file', 'define the class',");
        sb.AppendLine("  and 'add using directives' are NOT separate steps — they are one 'implement' step.");
        sb.AppendLine("- Prefer this shape: (1) implement the complete feature file(s); (2) write the complete");
        sb.AppendLine("  test file(s) that exercise runtime behavior; (3) build and run the tests to verify.");
        sb.AppendLine("- Collapse to fewer steps for small tasks; never pad the plan.");
        sb.AppendLine("Format: '1. <step description>'  — one step per line, nothing else.");

        var response = await _router.GenerateAsync(sb.ToString(), ModelTaskType.Coding, ct);
        if (string.IsNullOrWhiteSpace(response)) return null;

        return ParseNumberedSteps(response);
    }

    private static List<CodingPlanStep>? ParseNumberedSteps(string response)
    {
        var steps = new List<CodingPlanStep>();
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var rx = new Regex(@"^\s*(\d+)[.)]\s+(.+)$");

        foreach (var line in lines)
        {
            var m = rx.Match(line.Trim());
            if (!m.Success) continue;
            var desc = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(desc)) continue;
            steps.Add(new CodingPlanStep
            {
                StepNumber = steps.Count + 1,
                Description = desc,
                Status = "pending"
            });
        }

        return steps.Count >= 2 ? steps : null;
    }

    private static List<CodingPlanStep> BuildTemplatePlan(CodingContextPackage context)
    {
        // Coarse, file-complete steps. Each step produces complete files, not fragments —
        // this avoids the per-fragment NO_EDITS_NEEDED trap and keeps runs short.
        var steps = new List<CodingPlanStep>
        {
            new() { StepNumber = 1, Description = "Implement the complete feature file(s) required by the task, with correct numeric types and full working content.", Status = "pending" },
            new() { StepNumber = 2, Description = "Write the complete test file(s) that exercise the runtime behavior of the new code (assert observable results, not just compilation).", Status = "pending" },
            new() { StepNumber = 3, Description = "Build and run the tests to verify behavior; fix any failures.", Status = "pending" }
        };

        if (context.SuggestedCommands.Length > 0)
        {
            steps[2] = steps[2] with
            {
                Description = $"Build and run tests to verify behavior, then fix any failures. Commands: {string.Join(" | ", context.SuggestedCommands.Take(2))}"
            };
        }

        return steps;
    }

    private string GetModelName()
    {
        return Environment.GetEnvironmentVariable("DARCI_OLLAMA_CODING_MODEL")
            ?? Environment.GetEnvironmentVariable("DARCI_OLLAMA_MODEL")
            ?? "ollama";
    }
}
