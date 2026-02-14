using System.Threading.Channels;
using Darci.Shared;
using Darci.Memory;
using Darci.Tools.Cad;
using Darci.Tools.Ollama;
using Microsoft.Extensions.Logging;

namespace Darci.Tools;

/// <summary>
/// Implementation of DARCI's toolkit - coordinates all her capabilities.
/// </summary>
public class Toolkit : IToolkit
{
    private readonly ILogger<Toolkit> _logger;
    private readonly IOllamaClient _ollama;
    private readonly IMemoryStore _memory;
    private readonly ICadBridge _cad;
    private readonly Channel<OutgoingMessage> _outgoingMessages;

    private const int MaxCadIterations = 8;

    public Toolkit(
        ILogger<Toolkit> logger,
        IOllamaClient ollama,
        IMemoryStore memory,
        ICadBridge cad)
    {
        _logger = logger;
        _ollama = ollama;
        _memory = memory;
        _cad = cad;

        _outgoingMessages = Channel.CreateUnbounded<OutgoingMessage>();
    }

    public ChannelReader<OutgoingMessage> OutgoingMessages => _outgoingMessages.Reader;

    // === Communication ===

    public async Task SendMessage(string userId, string content)
    {
        await _outgoingMessages.Writer.WriteAsync(new OutgoingMessage
        {
            UserId = userId,
            Content = content,
            CreatedAt = DateTime.UtcNow
        });
    }

    public async Task<string> GenerateReply(ReplyContext context)
    {
        var prompt = BuildReplyPrompt(context);
        return await _ollama.Generate(prompt);
    }

    // === Language/Thinking ===

    public async Task<string> Generate(string prompt)
    {
        return await _ollama.Generate(prompt);
    }

    public async Task<MessageIntent> ClassifyIntent(string message)
    {
        var prompt = $@"Analyze this message and determine: Is the user ASKING ME TO DO SOMETHING, or just TALKING/SHARING?

If they are asking me to take action (research something, remind them, create something, etc.), classify the action type.
If they are just sharing thoughts, making conversation, or talking about what THEY will do, classify as conversation.

Respond with ONLY one word:
- conversation (user is chatting, sharing, or talking about their own plans)
- question (user is asking me a question)
- research (user is asking ME to research something)
- task (user is asking ME to do a task)
- reminder (user is asking ME to remind them)
- cad (user is asking ME to create/generate/design a 3D part, STL, or CAD model)
- feedback (user is giving feedback on my responses)

Message: ""{message}""

Classification:";

        var response = await _ollama.Generate(prompt);
        var intentStr = response.Trim().ToLowerInvariant().Replace(".", "");

        _logger.LogDebug("LLM classified message as: {Intent}", intentStr);

        var type = intentStr switch
        {
            "conversation" => IntentType.Conversation,
            "question" => IntentType.Question,
            "research" => IntentType.Research,
            "task" => IntentType.Task,
            "reminder" => IntentType.Reminder,
            "cad" => IntentType.CAD,
            "goalupdate" => IntentType.GoalUpdate,
            "statuscheck" => IntentType.StatusCheck,
            "feedback" => IntentType.Feedback,
            _ => IntentType.Conversation
        };

        return new MessageIntent
        {
            Type = type,
            Confidence = 0.8f
        };
    }

    // === Memory ===

    public async Task StoreMemory(string content, string[] tags)
    {
        await _memory.Store(content, tags);
    }

    public async Task<List<string>> RecallMemories(string query, int limit = 5)
    {
        var memories = await _memory.Recall(query, limit);
        return memories.Select(m => m.Content).ToList();
    }

    public async Task ConsolidateMemories()
    {
        await _memory.Consolidate();
    }

    // === Research ===

    public async Task<string> SearchWeb(string query)
    {
        // TODO: Implement web search (SearXNG, Serper API, etc.)
        _logger.LogWarning("Web search not implemented, using LLM knowledge for: {Query}", query);

        var prompt = $@"Based on your knowledge, provide information about: {query}

Be concise and factual. If you don't know something, say so.";

        return await _ollama.Generate(prompt);
    }

    // === Files ===

    public async Task<string> ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("File not found: {Path}", path);
            return "";
        }

        return await File.ReadAllTextAsync(path);
    }

    public async Task WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content);
        _logger.LogInformation("Wrote file: {Path}", path);
    }

    // === Goals ===

    public async Task<int> CreateGoal(string description, string userId)
    {
        // TODO: Wire up to GoalManager
        _logger.LogInformation("Creating goal for {User}: {Description}", userId, description);
        return 0;
    }

    public async Task ProgressGoal(int goalId)
    {
        // TODO: Wire up to GoalManager
        _logger.LogInformation("Progressing goal {GoalId}", goalId);
    }

    // ================================================================
    // === CAD Generation ===
    // ================================================================

    public async Task<CadPipelineResult> GenerateCAD(
        string description,
        CadDimensionSpec? dimensions = null,
        int maxIterations = 5)
    {
        maxIterations = Math.Clamp(maxIterations, 1, MaxCadIterations);

        var result = new CadPipelineResult
        {
            OriginalRequest = description,
            Iterations = new List<CadIterationLog>()
        };

        var sanitizedDescription = SanitizeForCadPrompt(description);

        // Step 1: Generate initial CadQuery script via LLM
        var initialPrompt = BuildInitialCadPrompt(sanitizedDescription, dimensions);
        var llmOutput = await _ollama.Generate(initialPrompt);
        var currentScript = NormalizeCadScript(ExtractCadScript(llmOutput));

        if (string.IsNullOrWhiteSpace(currentScript))
        {
            result.Success = false;
            result.Error = "Could not generate an initial CadQuery script from the description.";
            return result;
        }

        _logger.LogInformation("Generated initial CadQuery script ({Length} chars) for: {Desc}",
            currentScript.Length, description);

        // Step 2: Generate → Validate → Feedback loop
        CadGenerateResponse? bestPassingResult = null;
        int? bestPassingIteration = null;

        for (int i = 0; i < maxIterations; i++)
        {
            var iterLog = new CadIterationLog { Iteration = i, Script = currentScript };

            var cadResponse = await _cad.Generate(new CadGenerateRequest
            {
                Script = currentScript,
                Filename = $"part_v{i}.stl",
                Dimensions = dimensions
            });

            iterLog.Result = cadResponse;
            result.Iterations.Add(iterLog);

            if (cadResponse == null)
            {
                result.Success = false;
                result.Error = "CAD engine service is unreachable. Is the Python service running?";
                return result;
            }

            bool passed = cadResponse.Success && cadResponse.Validation?.Passed == true;

            if (passed)
            {
                bestPassingResult = cadResponse;
                bestPassingIteration = i;

                if (i == maxIterations - 1)
                {
                    _logger.LogInformation(
                        "Using validated CAD result from final iteration {Iter} without extra approval pass",
                        i);
                    result.Success = true;
                    result.FinalStlPath = cadResponse.StlPath;
                    result.FinalRenders = cadResponse.RenderImages;
                    result.FinalValidation = cadResponse.Validation;
                    result.ApprovedAtIteration = i;
                    return result;
                }

                var feedbackPrompt = await _cad.GetFeedbackPrompt(sanitizedDescription, cadResponse);

                if (feedbackPrompt == null)
                {
                    // Can't get feedback, but validation passed — accept
                    result.Success = true;
                    result.FinalStlPath = cadResponse.StlPath;
                    result.FinalRenders = cadResponse.RenderImages;
                    result.FinalValidation = cadResponse.Validation;
                    result.ApprovedAtIteration = i;
                    return result;
                }

                var evalResponse = await _ollama.Generate(feedbackPrompt);

                if (evalResponse.Trim().ToUpperInvariant().Contains("APPROVED"))
                {
                    _logger.LogInformation("CAD output APPROVED at iteration {Iter}", i);
                    result.Success = true;
                    result.FinalStlPath = cadResponse.StlPath;
                    result.FinalRenders = cadResponse.RenderImages;
                    result.FinalValidation = cadResponse.Validation;
                    result.ApprovedAtIteration = i;
                    return result;
                }

                // DARCI wants changes — check for oscillation
                var newScript = NormalizeCadScript(ExtractCadScript(evalResponse));
                if (string.IsNullOrWhiteSpace(newScript) || newScript == currentScript)
                {
                    result.Success = true;
                    result.FinalStlPath = cadResponse.StlPath;
                    result.FinalRenders = cadResponse.RenderImages;
                    result.FinalValidation = cadResponse.Validation;
                    result.ApprovedAtIteration = i;
                    return result;
                }

                currentScript = newScript;
            }
            else
            {
                // Validation failed — get feedback and fix
                var feedbackPrompt = await _cad.GetFeedbackPrompt(sanitizedDescription, cadResponse);

                if (feedbackPrompt == null)
                {
                    result.Success = false;
                    result.Error = "Failed to build feedback prompt for correction.";
                    return result;
                }

                var fixResponse = await _ollama.Generate(feedbackPrompt);
                var newScript = NormalizeCadScript(ExtractCadScript(fixResponse));

                if (string.IsNullOrWhiteSpace(newScript) || newScript == currentScript)
                {
                    iterLog.StoppedReason = "No meaningful change between iterations";
                    result.Success = false;
                    result.Error =
                        $"Could not resolve validation issues after {i + 1} attempts. " +
                        $"Last error: {cadResponse.Error ?? "validation failed"}";
                    return result;
                }

                currentScript = newScript;
            }
        }

        if (bestPassingResult != null)
        {
            _logger.LogInformation(
                "Reached iteration cap; returning best validated CAD result from iteration {Iter}",
                bestPassingIteration);
            result.Success = true;
            result.FinalStlPath = bestPassingResult.StlPath;
            result.FinalRenders = bestPassingResult.RenderImages;
            result.FinalValidation = bestPassingResult.Validation;
            result.ApprovedAtIteration = bestPassingIteration;
            return result;
        }

        result.Success = false;
        result.Error = $"Reached max iterations ({maxIterations}) without full approval.";
        return result;
    }

    public async Task<CadGenerateResponse?> ExecuteCADScript(
        string script,
        CadDimensionSpec? dimensions = null,
        string filename = "output.stl")
    {
        return await _cad.Generate(new CadGenerateRequest
        {
            Script = script,
            Filename = filename,
            Dimensions = dimensions
        });
    }

    public async Task<bool> IsCADEngineHealthy()
    {
        return await _cad.IsHealthy();
    }

    // ================================================================
    // === Private Helpers ===
    // ================================================================

    private string BuildReplyPrompt(ReplyContext context)
    {
        var memoriesSection = context.RelevantMemories.Any()
            ? $"\nRelevant memories:\n{string.Join("\n", context.RelevantMemories.Select(m => $"- {m}"))}"
            : "";

        var goalsSection = context.ActiveGoals.Any()
            ? $"\nActive goals for this user:\n{string.Join("\n", context.ActiveGoals.Select(g => $"- {g}"))}"
            : "";

        return $@"You are DARCI, a thoughtful AI assistant. You are warm, intelligent, and genuinely care about helping.

Current state: {context.DarciState}
{memoriesSection}
{goalsSection}

User ({context.UserId}): {context.UserMessage}

Respond naturally and helpfully. Keep your response concise unless the situation calls for detail.

DARCI:";
    }

    private string BuildInitialCadPrompt(string description, CadDimensionSpec? dimensions)
    {
        var dimSection = "";
        if (dimensions != null)
        {
            var parts = new List<string>();
            if (dimensions.LengthMm.HasValue) parts.Add($"Length: {dimensions.LengthMm}mm");
            if (dimensions.WidthMm.HasValue) parts.Add($"Width: {dimensions.WidthMm}mm");
            if (dimensions.HeightMm.HasValue) parts.Add($"Height: {dimensions.HeightMm}mm");
            dimSection = $"\nTarget dimensions:\n{string.Join("\n", parts)}\n";
        }

        return $@"You are generating a CadQuery (Python) script to create a 3D part for CNC machining.

RULES:
1. Your script MUST assign the final geometry to a variable called `result`.
   Example: result = cq.Workplane(""XY"").box(50, 30, 10)
2. You may only import `cadquery` (as `cq`) and `math`. Nothing else.
3. All dimensions are in millimeters.
4. Produce a single watertight solid — no disconnected bodies.
5. Use Workplane methods in lowercase (e.g., `.box`, `.rect`, `.circle`, `.hole`).
6. NEVER call constructors like `cq.Circle(...)`, `cq.Rect(...)`, or `cq.Box(...)`.
7. Output ONLY the Python script. No explanation, no markdown fences.

Part description: {description}
{dimSection}
Generate the CadQuery script now:";
    }

    private static string SanitizeForCadPrompt(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";

        var sanitized = input
            .Replace("```", "")
            .Replace("APPROVED", "[REDACTED]")
            .Replace("\\n\\n##", "");

        if (sanitized.Length > 2000)
            sanitized = sanitized[..2000];

        return sanitized.Trim();
    }

    private static string ExtractCadScript(string llmOutput)
    {
        if (string.IsNullOrWhiteSpace(llmOutput)) return "";

        var trimmed = llmOutput.Trim();

        // Check for markdown code fences
        if (trimmed.Contains("```python"))
        {
            var start = trimmed.IndexOf("```python") + "```python".Length;
            var end = trimmed.IndexOf("```", start);
            if (end > start) return trimmed[start..end].Trim();
        }

        if (trimmed.Contains("```"))
        {
            var start = trimmed.IndexOf("```") + 3;
            var lineEnd = trimmed.IndexOf('\n', start);
            if (lineEnd > start) start = lineEnd + 1;
            var end = trimmed.IndexOf("```", start);
            if (end > start) return trimmed[start..end].Trim();
        }

        // Raw Python
        if (trimmed.Contains("result") && trimmed.Contains("cq."))
            return trimmed;

        return "";
    }

    private static string NormalizeCadScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return "";

        // Common LLM mistakes: CadQuery API casing and constructor-style misuse.
        return script
            .Replace(".Circle(", ".circle(")
            .Replace(".Rect(", ".rect(")
            .Replace(".Box(", ".box(")
            .Replace("cq.Circle(", "cq.Workplane(\"XY\").circle(")
            .Replace("cq.Rect(", "cq.Workplane(\"XY\").rect(")
            .Replace("cq.Box(", "cq.Workplane(\"XY\").box(");
    }
}
