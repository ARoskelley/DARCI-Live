using System.Threading.Channels;
using System.Globalization;
using System.Text.RegularExpressions;
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
    private const int ComplexCadMinIterations = 7;

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

    public async Task SendMessage(string userId, string content, bool externalNotify = false)
    {
        await _outgoingMessages.Writer.WriteAsync(new OutgoingMessage
        {
            UserId = userId,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            ExternalNotify = externalNotify
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
        var isComplexRequest = IsComplexCadRequest(sanitizedDescription);
        var portalSpec = TryExtractPortalGearSpec(sanitizedDescription);
        var systemValidationNotes = portalSpec != null
            ? BuildPortalSystemValidationNotes(portalSpec)
            : new List<string>();
        result.SystemValidationNotes = systemValidationNotes;
        string? buildPlan = null;

        if (isComplexRequest && portalSpec == null)
        {
            maxIterations = Math.Max(maxIterations, ComplexCadMinIterations);
            buildPlan = await GenerateCadPlan(sanitizedDescription, dimensions);
        }

        if (portalSpec != null)
        {
            maxIterations = Math.Max(maxIterations, 6);
            buildPlan = BuildPortalGearBuildPlan(portalSpec, systemValidationNotes);
        }

        var requestContext = string.IsNullOrWhiteSpace(buildPlan)
            ? sanitizedDescription
            : $"{sanitizedDescription}\n\nBuild nodes:\n{buildPlan}";

        if (portalSpec != null)
        {
            requestContext += "\n\nPortal gear spec:\n" + DescribePortalGearSpec(portalSpec);
        }

        if (systemValidationNotes.Count > 0)
        {
            requestContext += "\n\nSystem validation notes:\n" + string.Join("\n", systemValidationNotes.Select(n => $"- {n}"));
        }

        // Step 1: Build initial CadQuery script
        string currentScript;
        if (portalSpec != null && IsDeterministicPortalRole(portalSpec.Role))
        {
            currentScript = BuildPortalGearCadScript(portalSpec);
            _logger.LogInformation(
                "Using deterministic CAD strategy: portal-gear role={Role} teeth={Teeth}, module={Module}",
                portalSpec.Role, portalSpec.Teeth, portalSpec.Module);
        }
        else
        {
            var initialPrompt = BuildInitialCadPrompt(sanitizedDescription, dimensions, buildPlan);
            var llmOutput = await _ollama.Generate(initialPrompt);
            currentScript = NormalizeCadScript(ExtractCadScript(llmOutput));
        }

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

                var feedbackPrompt = await _cad.GetFeedbackPrompt(requestContext, cadResponse);

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
                var feedbackPrompt = await _cad.GetFeedbackPrompt(requestContext, cadResponse);

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

    private string BuildInitialCadPrompt(
        string description,
        CadDimensionSpec? dimensions,
        string? buildPlan = null)
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

        var planSection = string.IsNullOrWhiteSpace(buildPlan)
            ? ""
            : $"\nSuggested build nodes for this part:\n{buildPlan}\n";

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
8. For complex parts, keep a stable parameter block at the top and build in stages:
   base body -> primary features -> secondary details -> finishing (chamfers/fillets).
9. Minimize changes to stable geometry when adding later features.

Part description: {description}
{dimSection}
{planSection}
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

    private async Task<string?> GenerateCadPlan(
        string description,
        CadDimensionSpec? dimensions)
    {
        try
        {
            var planPrompt = BuildCadPlanPrompt(description, dimensions);
            var response = await _ollama.Generate(planPrompt);
            if (string.IsNullOrWhiteSpace(response))
                return null;

            var cleaned = response.Trim();
            if (cleaned.Length > 1500)
                cleaned = cleaned[..1500];
            return cleaned;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to generate CAD build plan; continuing without plan");
            return null;
        }
    }

    private static bool IsComplexCadRequest(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;

        var text = description.ToLowerInvariant();
        var keywords = new[]
        {
            "gear", "spur gear", "helical", "involute", "ring gear",
            "portal", "differential", "automotive", "transmission",
            "spline", "bearing", "threads", "threaded", "loft", "sweep",
            "multiple holes", "chamfer", "fillet", "tooth", "teeth"
        };

        var matches = keywords.Count(text.Contains);
        return matches >= 2 || text.Length > 180;
    }

    private static string BuildCadPlanPrompt(string description, CadDimensionSpec? dimensions)
    {
        var dimParts = new List<string>();
        if (dimensions?.LengthMm is { } l) dimParts.Add($"length={l}mm");
        if (dimensions?.WidthMm is { } w) dimParts.Add($"width={w}mm");
        if (dimensions?.HeightMm is { } h) dimParts.Add($"height={h}mm");
        var dimLine = dimParts.Count > 0
            ? $"Known dimensions: {string.Join(", ", dimParts)}"
            : "Known dimensions: none specified";

        return $@"Plan this CAD model as build nodes for a CadQuery script.
Return ONLY a concise numbered list (max 8 steps), no code.
Each step should represent a stable modeling node:
1) parameters, 2) base body, 3) primary features, 4) detail features, 5) finishing.
If the part is a gear, explicitly include tooth profile/tooth pattern node and bore node.

Part: {description}
{dimLine}";
    }

    private static PortalGearSpec? TryExtractPortalGearSpec(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var text = description.ToLowerInvariant();
        var hasPortalContext = text.Contains("portal gear") || (text.Contains("portal") && text.Contains("gear"));
        var hasGearContext = text.Contains("gear") || text.Contains("teeth") || text.Contains("tooth");
        if (!hasPortalContext && !hasGearContext)
            return null;

        var role = ExtractPortalGearRole(text);
        var pair = ExtractPortalGearPair(text);

        var spec = new PortalGearSpec
        {
            Teeth = ExtractInt(text, @"(\d+)\s*(?:teeth|tooth)"),
            Module = ExtractDouble(text, @"module\s*([0-9]+(?:\.[0-9]+)?)"),
            PressureAngleDeg = ExtractDouble(text, @"pressure angle\s*([0-9]+(?:\.[0-9]+)?)")
                ?? ExtractDouble(text, @"([0-9]+(?:\.[0-9]+)?)\s*(?:deg|degree|degrees).{0,12}pressure"),
            FaceWidthMm = ExtractDouble(text, @"([0-9]+(?:\.[0-9]+)?)\s*mm.{0,16}(?:face width|thick|thickness)")
                ?? ExtractDouble(text, @"face width.{0,10}?([0-9]+(?:\.[0-9]+)?)\s*mm"),
            BoreDiameterMm = ExtractDouble(text, @"(?:through[- ]?bore|center bore|centre bore|bore)\b[^0-9]{0,12}([0-9]+(?:\.[0-9]+)?)\s*mm")
                ?? ExtractDouble(text, @"([0-9]+(?:\.[0-9]+)?)\s*mm[^,.]{0,12}(?:through[- ]?bore|center bore|centre bore)\b"),
            ChamferMm = ExtractDouble(text, @"([0-9]+(?:\.[0-9]+)?)\s*mm.{0,20}chamfer"),
            AxleDiameterMm = ExtractDouble(text, @"([0-9]+(?:\.[0-9]+)?)\s*mm.{0,20}(?:axle|shaft).{0,10}(?:diameter|dia)?")
                ?? ExtractDouble(text, @"(?:axle|shaft).{0,20}?([0-9]+(?:\.[0-9]+)?)\s*mm"),
            HubDiameterMm = ExtractDouble(text, @"([0-9]+(?:\.[0-9]+)?)\s*mm.{0,16}(?:hub diameter|hub od|hub outside diameter)"),
            HubThicknessMm = ExtractDouble(text, @"([0-9]+(?:\.[0-9]+)?)\s*mm.{0,16}(?:hub thickness|hub width)"),
            Role = role,
            DriveTeeth = pair.driveTeeth,
            DrivenTeeth = pair.drivenTeeth,
            TargetRatio = ExtractDouble(text, @"(?:ratio|gear ratio)\s*(?:of|=|:)?\s*([0-9]+(?:\.[0-9]+)?)"),
            CenterDistanceMm = ExtractDouble(text, @"([0-9]+(?:\.[0-9]+)?)\s*mm.{0,16}(?:center distance|centre distance)"),
            DriveAxleDiameterMm = ExtractDouble(text, @"drive axle.{0,16}?([0-9]+(?:\.[0-9]+)?)\s*mm"),
            DrivenAxleDiameterMm = ExtractDouble(text, @"(?:driven|idler|output) axle.{0,16}?([0-9]+(?:\.[0-9]+)?)\s*mm"),
            AxleClearanceMm = ExtractDouble(text, @"axle clearance.{0,12}?([0-9]+(?:\.[0-9]+)?)\s*mm")
                ?? ExtractDouble(text, @"clearance.{0,12}?([0-9]+(?:\.[0-9]+)?)\s*mm"),
        };

        var known = 0;
        if (spec.Teeth.HasValue) known++;
        if (spec.Module.HasValue) known++;
        if (spec.FaceWidthMm.HasValue) known++;
        if (spec.BoreDiameterMm.HasValue) known++;
        if (spec.PressureAngleDeg.HasValue) known++;

        if (!hasPortalContext && known < 4)
            return null;

        spec.Teeth ??= 20;
        spec.Module ??= 2.0;
        spec.PressureAngleDeg ??= 20.0;
        spec.FaceWidthMm ??= 10.0;
        spec.BoreDiameterMm ??= 10.0;
        spec.ChamferMm ??= 0.0;
        spec.AxleClearanceMm ??= 0.15;

        if (spec.Role == PortalGearRole.Drive && spec.DriveTeeth.HasValue)
            spec.Teeth = spec.DriveTeeth;
        else if (spec.Role == PortalGearRole.Driven && spec.DrivenTeeth.HasValue)
            spec.Teeth = spec.DrivenTeeth;
        else if ((spec.Role == PortalGearRole.Idler || spec.Role == PortalGearRole.Unknown)
            && !spec.Teeth.HasValue
            && spec.DrivenTeeth.HasValue)
            spec.Teeth = spec.DrivenTeeth;

        if (spec.Role == PortalGearRole.Drive && spec.DriveAxleDiameterMm.HasValue)
            spec.AxleDiameterMm = spec.DriveAxleDiameterMm;
        else if (spec.Role == PortalGearRole.Driven && spec.DrivenAxleDiameterMm.HasValue)
            spec.AxleDiameterMm = spec.DrivenAxleDiameterMm;

        return spec;
    }

    private static int? ExtractInt(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!match.Success || match.Groups.Count < 2)
            return null;
        return int.TryParse(match.Groups[1].Value, out var v) ? v : null;
    }

    private static double? ExtractDouble(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!match.Success || match.Groups.Count < 2)
            return null;
        return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private static PortalGearRole ExtractPortalGearRole(string text)
    {
        if (text.Contains("ring gear") || text.Contains("internal gear"))
            return PortalGearRole.Ring;
        if (text.Contains("pinion") || text.Contains("drive gear") || text.Contains("input gear"))
            return PortalGearRole.Drive;
        if (text.Contains("driven gear") || text.Contains("output gear"))
            return PortalGearRole.Driven;
        if (text.Contains("idler"))
            return PortalGearRole.Idler;
        return PortalGearRole.Unknown;
    }

    private static (int? driveTeeth, int? drivenTeeth) ExtractPortalGearPair(string text)
    {
        var drive = ExtractInt(text, @"(?:\bdrive\b|\bpinion\b|\binput\b)\s*(?:gear)?[^0-9]{0,16}(\d+)\s*(?:teeth|tooth)")
            ?? ExtractInt(text, @"(\d+)\s*(?:teeth|tooth)\s*(?:\bdrive\b|\bpinion\b|\binput\b)");
        var driven = ExtractInt(text, @"(?:\bdriven\b|\boutput\b|\bidler\b)\s*(?:gear)?[^0-9]{0,16}(\d+)\s*(?:teeth|tooth)")
            ?? ExtractInt(text, @"(\d+)\s*(?:teeth|tooth)\s*(?:\bdriven\b|\boutput\b|\bidler\b)");

        if (drive.HasValue && driven.HasValue)
            return (drive, driven);

        var directional = Regex.Match(
            text,
            @"(\d+)\s*(?:teeth|tooth)\s*drive[^0-9]{0,32}(\d+)\s*(?:teeth|tooth)\s*driven");
        if (directional.Success && directional.Groups.Count >= 3)
        {
            var d = int.TryParse(directional.Groups[1].Value, out var dv) ? dv : (int?)null;
            var o = int.TryParse(directional.Groups[2].Value, out var ov) ? ov : (int?)null;
            return (drive ?? d, driven ?? o);
        }

        var pair = Regex.Match(text, @"(\d+)\s*(?:teeth|tooth)[^0-9]{0,20}(?:and|to|/|with)[^0-9]{0,20}(\d+)\s*(?:teeth|tooth)");
        if (pair.Success && pair.Groups.Count >= 3)
        {
            var a = int.TryParse(pair.Groups[1].Value, out var av) ? av : (int?)null;
            var b = int.TryParse(pair.Groups[2].Value, out var bv) ? bv : (int?)null;
            return (drive ?? a, driven ?? b);
        }

        return (drive, driven);
    }

    private static bool IsDeterministicPortalRole(PortalGearRole role)
    {
        return role != PortalGearRole.Ring;
    }

    private static List<string> BuildPortalSystemValidationNotes(PortalGearSpec spec)
    {
        var notes = new List<string>();

        if (spec.DriveTeeth.HasValue && spec.DrivenTeeth.HasValue && spec.Module.HasValue)
        {
            var expectedRatio = (double)spec.DrivenTeeth.Value / spec.DriveTeeth.Value;
            notes.Add($"Pair ratio (driven/drive) = {expectedRatio.ToString("0.###", CultureInfo.InvariantCulture)}.");

            var expectedCenter = 0.5 * spec.Module.Value * (spec.DriveTeeth.Value + spec.DrivenTeeth.Value);
            notes.Add($"Expected center distance = {expectedCenter.ToString("0.###", CultureInfo.InvariantCulture)} mm.");

            if (spec.TargetRatio.HasValue)
            {
                var ratioDelta = Math.Abs(spec.TargetRatio.Value - expectedRatio);
                if (ratioDelta <= 0.03)
                    notes.Add($"Target ratio {spec.TargetRatio.Value.ToString("0.###", CultureInfo.InvariantCulture)} is compatible.");
                else
                    notes.Add($"Target ratio mismatch: requested {spec.TargetRatio.Value.ToString("0.###", CultureInfo.InvariantCulture)} vs expected {expectedRatio.ToString("0.###", CultureInfo.InvariantCulture)}.");
            }

            if (spec.CenterDistanceMm.HasValue)
            {
                var cdDelta = Math.Abs(spec.CenterDistanceMm.Value - expectedCenter);
                if (cdDelta <= 0.25)
                    notes.Add($"Center distance {spec.CenterDistanceMm.Value.ToString("0.###", CultureInfo.InvariantCulture)} mm is compatible.");
                else
                    notes.Add($"Center distance mismatch: requested {spec.CenterDistanceMm.Value.ToString("0.###", CultureInfo.InvariantCulture)} mm vs expected {expectedCenter.ToString("0.###", CultureInfo.InvariantCulture)} mm.");
            }
        }

        if (spec.AxleDiameterMm.HasValue && spec.BoreDiameterMm.HasValue)
        {
            var minBore = spec.AxleDiameterMm.Value + (spec.AxleClearanceMm ?? 0.15);
            if (spec.BoreDiameterMm.Value >= minBore)
                notes.Add($"Axle fit check passed: bore {spec.BoreDiameterMm.Value.ToString("0.###", CultureInfo.InvariantCulture)} mm >= min {minBore.ToString("0.###", CultureInfo.InvariantCulture)} mm.");
            else
                notes.Add($"Axle fit check failed: bore {spec.BoreDiameterMm.Value.ToString("0.###", CultureInfo.InvariantCulture)} mm < min {minBore.ToString("0.###", CultureInfo.InvariantCulture)} mm.");
        }

        if (spec.Role == PortalGearRole.Ring)
        {
            notes.Add("Ring gear requested: deterministic external-gear builder is bypassed; using guided LLM fallback.");
        }

        return notes;
    }

    private static string DescribePortalGearSpec(PortalGearSpec spec)
    {
        static string F(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "null";
        static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "null";

        return string.Join("\n", new[]
        {
            $"teeth={I(spec.Teeth)}",
            $"module={F(spec.Module)}",
            $"pressure_angle_deg={F(spec.PressureAngleDeg)}",
            $"face_width_mm={F(spec.FaceWidthMm)}",
            $"bore_diameter_mm={F(spec.BoreDiameterMm)}",
            $"chamfer_mm={F(spec.ChamferMm)}",
            $"role={spec.Role}",
            $"drive_teeth={I(spec.DriveTeeth)}",
            $"driven_teeth={I(spec.DrivenTeeth)}",
            $"target_ratio={F(spec.TargetRatio)}",
            $"center_distance_mm={F(spec.CenterDistanceMm)}",
            $"axle_diameter_mm={F(spec.AxleDiameterMm)}",
            $"drive_axle_diameter_mm={F(spec.DriveAxleDiameterMm)}",
            $"driven_axle_diameter_mm={F(spec.DrivenAxleDiameterMm)}",
            $"axle_clearance_mm={F(spec.AxleClearanceMm)}",
            $"hub_diameter_mm={F(spec.HubDiameterMm)}",
            $"hub_thickness_mm={F(spec.HubThicknessMm)}",
        });
    }

    private static string BuildPortalGearBuildPlan(PortalGearSpec spec, List<string> systemNotes)
    {
        var lines = new List<string>
        {
            "1) Parameter block: teeth/module/pressure-angle/face-width/bore/chamfer/axle-hub.",
            "2) Compute diameters: pitch, root, outer and tooth angular pitch.",
            "3) Build root blank as extruded cylinder.",
            "4) Build one tooth profile and polar-pattern union across all teeth.",
            "5) Add optional hub boss for portal-lift axle support.",
            "6) Cut center through-bore and optional axle clearance bore.",
            "7) Apply top and bottom chamfers within safe limits.",
            $"8) Validate against portal spec: teeth={spec.Teeth}, module={(spec.Module?.ToString(CultureInfo.InvariantCulture) ?? "null")}."
        };

        if (systemNotes.Count > 0)
        {
            lines.Add("9) Enforce system checks:");
            lines.AddRange(systemNotes.Select(n => $"   - {n}"));
        }

        return string.Join("\n", lines);
    }

    private static string BuildPortalGearCadScript(PortalGearSpec spec)
    {
        var teeth = Math.Max(8, spec.Teeth ?? 20);
        var module = Math.Max(0.5, spec.Module ?? 2.0);
        var pressure = Math.Clamp(spec.PressureAngleDeg ?? 20.0, 12.0, 30.0);
        var faceWidth = Math.Max(2.0, spec.FaceWidthMm ?? 10.0);
        var requestedBore = Math.Max(1.0, spec.BoreDiameterMm ?? 10.0);
        var chamfer = Math.Max(0.0, spec.ChamferMm ?? 0.0);
        var axle = spec.AxleDiameterMm ?? requestedBore;
        var minBoreForAxle = axle + (spec.AxleClearanceMm ?? 0.15);
        var bore = Math.Max(requestedBore, minBoreForAxle);
        var defaultHubMul = spec.Role == PortalGearRole.Idler ? 3.4 : 4.0;
        var hubDiameter = spec.HubDiameterMm ?? Math.Max(bore + 6.0, module * defaultHubMul);
        var hubThickness = spec.HubThicknessMm ?? Math.Min(faceWidth * 0.65, Math.Max(3.0, module * 2.0));

        string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        return $@"import cadquery as cq
import math

teeth = {teeth}
module = {F(module)}
pressure_angle_deg = {F(pressure)}
face_width = {F(faceWidth)}
bore_diameter = {F(bore)}
chamfer_size = {F(chamfer)}
hub_diameter = {F(hubDiameter)}
hub_thickness = {F(hubThickness)}
axle_clearance_diameter = {F(axle)}
role = ""{spec.Role.ToString().ToLowerInvariant()}""

pitch_d = module * teeth
addendum = module
dedendum = 1.25 * module
root_d = max(pitch_d - 2.0 * dedendum, bore_diameter + 2.0)
outer_d = pitch_d + 2.0 * addendum

tooth_pitch = 2.0 * math.pi / teeth
pressure_factor = max(0.22, min(0.38, 0.30 + (20.0 - pressure_angle_deg) * 0.004))
tooth_half = tooth_pitch * 0.47
tooth_top_half = tooth_pitch * pressure_factor

r_root = root_d / 2.0
r_outer = outer_d / 2.0

tooth_points = []
for ang in (-tooth_half, -tooth_top_half, tooth_top_half, tooth_half):
    use_outer = abs(ang) <= tooth_top_half + 1e-9
    r = r_outer if use_outer else r_root
    tooth_points.append((r * math.cos(ang), r * math.sin(ang)))

blank = cq.Workplane(""XY"").circle(r_root).extrude(face_width)
tooth = cq.Workplane(""XY"").polyline(tooth_points).close().extrude(face_width)

gear = blank
for i in range(teeth):
    gear = gear.union(
        tooth.rotate((0, 0, 0), (0, 0, 1), i * (360.0 / teeth))
    )

if hub_thickness > 0.0 and hub_diameter > (bore_diameter + 1.0):
    hub = cq.Workplane(""XY"").circle(hub_diameter / 2.0).extrude(hub_thickness)
    if hub_thickness < face_width:
        hub = hub.translate((0, 0, (face_width - hub_thickness) / 2.0))
    gear = gear.union(hub)

safe_chamfer = min(chamfer_size, module * 0.75, face_width * 0.25)
if safe_chamfer > 0.01:
    gear = gear.faces("">Z"").edges().chamfer(safe_chamfer)
    gear = gear.faces(""<Z"").edges().chamfer(safe_chamfer)

result = gear.faces("">Z"").workplane().hole(bore_diameter)

if axle_clearance_diameter > (bore_diameter + 0.05):
    result = result.faces("">Z"").workplane().hole(axle_clearance_diameter)
";
    }

    private sealed class PortalGearSpec
    {
        public int? Teeth { get; set; }
        public double? Module { get; set; }
        public double? PressureAngleDeg { get; set; }
        public double? FaceWidthMm { get; set; }
        public double? BoreDiameterMm { get; set; }
        public double? ChamferMm { get; set; }
        public PortalGearRole Role { get; set; } = PortalGearRole.Unknown;
        public int? DriveTeeth { get; set; }
        public int? DrivenTeeth { get; set; }
        public double? TargetRatio { get; set; }
        public double? CenterDistanceMm { get; set; }
        public double? AxleDiameterMm { get; set; }
        public double? DriveAxleDiameterMm { get; set; }
        public double? DrivenAxleDiameterMm { get; set; }
        public double? AxleClearanceMm { get; set; }
        public double? HubDiameterMm { get; set; }
        public double? HubThicknessMm { get; set; }
    }

    private enum PortalGearRole
    {
        Unknown,
        Drive,
        Driven,
        Idler,
        Ring
    }
}
