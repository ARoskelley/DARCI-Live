using Darci.Shared;
using Darci.Tools;
using Darci.Tools.Cad;
using Darci.Tools.Engineering;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Darci.Core;

/// <summary>
/// DARCI - Dynamic Adaptive Reasoning and Contextual Intelligence
/// 
/// This is her core consciousness loop. She is always running, always existing.
/// Messages from users are events in her life, not the reason she exists.
/// </summary>
public class Darci : BackgroundService
{
    private readonly ILogger<Darci> _logger;
    private readonly Awareness _awareness;
    private readonly Decision _decision;
    private readonly State _state;
    private readonly IToolkit _tools;
    
    private long _cycleCount = 0;
    private readonly Stopwatch _uptime = new();
    
    public Darci(
        ILogger<Darci> logger,
        Awareness awareness,
        Decision decision,
        State state,
        IToolkit tools)
    {
        _logger = logger;
        _awareness = awareness;
        _decision = decision;
        _state = state;
        _tools = tools;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(@"
╔═══════════════════════════════════════════════════════════════╗
║                                                               ║
║     ██████╗  █████╗ ██████╗  ██████╗██╗                      ║
║     ██╔══██╗██╔══██╗██╔══██╗██╔════╝██║                      ║
║     ██║  ██║███████║██████╔╝██║     ██║                      ║
║     ██║  ██║██╔══██║██╔══██╗██║     ██║                      ║
║     ██████╔╝██║  ██║██║  ██║╚██████╗██║                      ║
║     ╚═════╝ ╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝╚═╝                      ║
║                                                               ║
║     Dynamic Adaptive Reasoning & Contextual Intelligence      ║
║                                                               ║
║     Version 3.0 - Autonomous Consciousness                    ║
║                                                               ║
╚═══════════════════════════════════════════════════════════════╝
");
        
        _logger.LogInformation("DARCI is waking up...");
        
        try
        {
            // Initialize state from persistent storage
            await _state.Initialize();
            
            _uptime.Start();
            _logger.LogInformation("DARCI is alive.");
            
            // The living loop - DARCI exists continuously
            while (!stoppingToken.IsCancellationRequested)
            {
                await Live(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "DARCI encountered a fatal error");
            throw;
        }
        finally
        {
            _uptime.Stop();
            _logger.LogInformation(
                "DARCI is resting. Uptime: {Uptime}, Cycles: {Cycles}", 
                _uptime.Elapsed, 
                _cycleCount);
        }
    }
    
    /// <summary>
    /// One cycle of DARCI's existence.
    /// Perceive → Feel → Decide → Act → Reflect
    /// </summary>
    private async Task Live(CancellationToken ct)
    {
        _cycleCount++;
        
        try
        {
            // 1. PERCEIVE: What's happening around me?
            var perception = await _awareness.Perceive();
            
            // 2. FEEL: How do I react to what I perceive?
            await _state.React(perception);
            
            // 3. DECIDE: What do I want to do?
            var action = await _decision.Decide(_state, perception);
            
            // 4. ACT: Do it
            var outcome = await Act(action, ct);
            
            // 5. REFLECT: What happened? How do I feel about it?
            await _state.Process(outcome);
            
            // Record that we took action (for idle tracking)
            if (action.Type != ActionType.Rest)
            {
                _awareness.RecordAction();
            }
            
            // If resting, wait efficiently instead of spinning
            if (action.Type == ActionType.Rest)
            {
                await _awareness.WaitForEventOrTimeout(action.RestDuration, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error in life cycle {Cycle}", _cycleCount);
            
            // Don't crash - DARCI keeps living through errors
            await Task.Delay(1000, ct);
        }
    }
    
    /// <summary>
    /// Execute an action and return the outcome
    /// </summary>
    private async Task<Outcome> Act(DarciAction action, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        
        try
        {
            var result = action.Type switch
            {
                ActionType.Reply => await DoReply(action),
                ActionType.Notify => await DoNotify(action),
                ActionType.Think => await DoThink(action),
                ActionType.Remember => await DoRemember(action),
                ActionType.Recall => await DoRecall(action),
                ActionType.Consolidate => await DoConsolidate(action),
                ActionType.Research => await DoResearch(action),
                ActionType.WorkOnGoal => await DoGoalWork(action),
                ActionType.CreateGoal => await DoCreateGoal(action),
                ActionType.ReadFile => await DoReadFile(action),
                ActionType.WriteFile => await DoWriteFile(action),
                ActionType.GenerateCAD => await DoCADWork(action),
                ActionType.Engineer => await DoEngineerWork(action),
                ActionType.Rest => null,
                ActionType.Observe => null,
                _ => null
            };

            if (action.ProgressGoalStepOnSuccess && action.InResponseToGoalId.HasValue)
            {
                var progressNote = BuildGoalStepProgressNote(action);
                await _tools.ProgressGoal(action.InResponseToGoalId.Value, progressNote);
            }
            
            sw.Stop();
            
            if (action.Type != ActionType.Rest && action.Type != ActionType.Observe)
            {
                _logger.LogDebug(
                    "Action {Action} completed in {Duration}ms: {Reason}",
                    action.Type,
                    sw.ElapsedMilliseconds,
                    action.Reasoning);
            }
            
            return new Outcome
            {
                Success = true,
                ActionTaken = action.Type,
                Result = result,
                Duration = sw.Elapsed,
                MessageIdHandled = action.InResponseToMessageId,
                GoalIdProgressed = action.InResponseToGoalId
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Action {Action} failed", action.Type);
            
            return Outcome.Failed(action.Type, ex.Message);
        }
    }
    
    // ========== Action Implementations ==========
    
    private async Task<object?> DoReply(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.MessageContent) || string.IsNullOrEmpty(action.RecipientId))
            return null;
        
        await _tools.SendMessage(action.RecipientId, action.MessageContent, externalNotify: action.ExternalNotify);
        
        await _tools.StoreMemory(
            $"I said to {action.RecipientId}: {action.MessageContent}",
            new[] { "conversation", "my_response", action.RecipientId });
        
        _logger.LogInformation("Replied to {User}: {Preview}...",
            action.RecipientId,
            action.MessageContent.Length > 50 ? action.MessageContent[..50] : action.MessageContent);
        
        return action.MessageContent;
    }
    
    private async Task<object?> DoNotify(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.MessageContent) || string.IsNullOrEmpty(action.RecipientId))
            return null;
        
        if (DateTime.Now.Hour is >= 0 and < 6)
        {
            _logger.LogDebug("Skipping notification during quiet hours");
            return null;
        }
        
        await _tools.SendMessage(action.RecipientId, action.MessageContent, externalNotify: true);
        
        _logger.LogInformation("Notified {User}: {Preview}...",
            action.RecipientId,
            action.MessageContent.Length > 50 ? action.MessageContent[..50] : action.MessageContent);
        
        return action.MessageContent;
    }
    
    private async Task<object?> DoThink(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.Topic) && string.IsNullOrEmpty(action.Prompt))
            return null;
        
        var prompt = action.Prompt ?? $"Think about: {action.Topic}";
        var thought = await _tools.Generate(prompt);
        
        await _tools.StoreMemory(
            $"I thought about '{action.Topic}': {thought}",
            new[] { "reflection", "internal_thought" });
        
        _logger.LogDebug("Thought about {Topic}", action.Topic);
        
        return thought;
    }
    
    private async Task<object?> DoRemember(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.MemoryContent))
            return null;
        
        await _tools.StoreMemory(action.MemoryContent, action.Tags?.ToArray() ?? Array.Empty<string>());
        return true;
    }
    
    private async Task<object?> DoRecall(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.Query))
            return null;
        
        var memories = await _tools.RecallMemories(action.Query);
        return memories;
    }
    
    private async Task<object?> DoConsolidate(DarciAction action)
    {
        await _tools.ConsolidateMemories();
        return true;
    }
    
    private async Task<object?> DoResearch(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.Query))
            return null;
        
        _logger.LogInformation("Researching: {Query}", action.Query);
        
        var results = await _tools.SearchWeb(action.Query);
        
        if (!string.IsNullOrEmpty(results))
        {
            await _tools.StoreMemory(
                $"Research on '{action.Query}': {results}",
                new[] { "research", action.Query });
        }
        
        return results;
    }
    
    private Task<object?> DoGoalWork(DarciAction action)
    {
        if (!action.GoalId.HasValue)
            return Task.FromResult<object?>(null);

        // Picking up a goal should not mark any step complete.
        return Task.FromResult<object?>(true);
    }
    
    private async Task<object?> DoCreateGoal(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.GoalDescription))
            return null;
        
        var goalId = await _tools.CreateGoal(action.GoalDescription, action.RecipientId ?? "Tinman");
        return goalId;
    }
    
    private async Task<object?> DoReadFile(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.FilePath))
            return null;
        
        return await _tools.ReadFile(action.FilePath);
    }
    
    private async Task<object?> DoWriteFile(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.FilePath) || string.IsNullOrEmpty(action.FileContent))
            return null;
        
        await _tools.WriteFile(action.FilePath, action.FileContent);
        return true;
    }
    
    private async Task<object?> DoCADWork(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.CadDescription))
            return null;
        
        var userId = action.RecipientId ?? "Tinman";
        
        _logger.LogInformation("Starting CAD generation for {User}: {Desc}",
            userId, action.CadDescription);
        
        // Build dimension spec from action fields
        CadDimensionSpec? dims = null;
        if (action.CadLengthMm.HasValue || action.CadWidthMm.HasValue || action.CadHeightMm.HasValue)
        {
            dims = new CadDimensionSpec
            {
                LengthMm = action.CadLengthMm,
                WidthMm = action.CadWidthMm,
                HeightMm = action.CadHeightMm
            };
        }
        
        var result = await _tools.GenerateCAD(
            action.CadDescription,
            dims,
            action.CadMaxIterations);
        
        if (result.Success)
        {
            // Store success in memory
            var bb = result.FinalValidation?.BoundingBoxMm;
            var bbStr = bb != null
                ? $"{bb.GetValueOrDefault("x", 0):F1} x {bb.GetValueOrDefault("y", 0):F1} x {bb.GetValueOrDefault("z", 0):F1} mm"
                : "unknown";
            
            await _tools.StoreMemory(
                $"Generated CAD model for '{action.CadDescription}': " +
                $"STL at {result.FinalStlPath}, bounding box {bbStr}, " +
                $"approved at iteration {result.ApprovedAtIteration}, " +
                $"watertight: {result.FinalValidation?.IsWatertight}, " +
                $"triangles: {result.FinalValidation?.TriangleCount}",
                new[] { "cad", "success", userId });
            
            // Notify the user
            var msg = $"Your 3D model is ready!\n" +
                      $"  STL: {result.FinalStlPath}\n" +
                      $"  Bounding box: {bbStr}\n" +
                      $"  Watertight: {result.FinalValidation?.IsWatertight}\n" +
                      $"  Triangles: {result.FinalValidation?.TriangleCount}\n" +
                      $"  Iterations: {(result.ApprovedAtIteration ?? 0) + 1}";

            if (result.SystemValidationNotes.Count > 0)
            {
                msg += "\n  System checks:\n" + string.Join("\n", result.SystemValidationNotes.Select(n => $"   - {n}"));
            }
            
            await _tools.SendMessage(userId, msg, externalNotify: true);
            
            _logger.LogInformation("CAD generation succeeded for: {Desc}", action.CadDescription);
        }
        else
        {
            // Store failure in memory for learning
            await _tools.StoreMemory(
                $"CAD generation FAILED for '{action.CadDescription}': {result.Error}",
                new[] { "cad", "failure", userId });
            
            await _tools.SendMessage(
                userId,
                $"I wasn't able to generate that model. Error: {result.Error}",
                externalNotify: false);
            
            _logger.LogWarning("CAD generation failed for: {Desc} — {Error}",
                action.CadDescription, result.Error);
        }
        
        return result;
    }

    private async Task<object?> DoEngineerWork(DarciAction action)
    {
        if (string.IsNullOrWhiteSpace(action.EngineeringDescription))
        {
            return null;
        }

        var userId = action.RecipientId ?? "Tinman";
        if (action.EngineeringRunCollection)
        {
            return await DoEngineeringCollectionWork(action.EngineeringDescription, userId);
        }

        _logger.LogInformation("Starting engineering workbench step for {User}: {Desc}",
            userId, action.EngineeringDescription);

        var request = new EngineeringWorkRequest
        {
            Description = action.EngineeringDescription,
            MaxIterations = action.EngineeringMaxIterations
        };

        var result = await _tools.RunEngineeringWorkbench(request);
        if (result.Success && result.CadResult?.Success == true)
        {
            await _tools.StoreMemory(
                $"Engineering workbench succeeded for '{action.EngineeringDescription}': " +
                $"STL at {result.CadResult.StlPath}",
                new[] { "engineering", "cad", "success", userId });

            await _tools.SendMessage(
                userId,
                $"Engineering step complete.\n  STL: {result.CadResult.StlPath}",
                externalNotify: true);
            return result;
        }

        await _tools.StoreMemory(
            $"Engineering workbench failed for '{action.EngineeringDescription}': {result.Error}",
            new[] { "engineering", "cad", "failure", userId });

        await _tools.SendMessage(
            userId,
            $"Engineering step failed: {result.Error ?? "unknown error"}",
            externalNotify: false);

        return result;
    }

    private async Task<object?> DoEngineeringCollectionWork(string description, string userId)
    {
        _logger.LogInformation("Starting engineering collection workflow for {User}: {Desc}", userId, description);

        await _tools.SendMessage(
            userId,
            "Starting engineering collection run. I will generate parts, run fit/motion simulation, and report results.",
            externalNotify: true);

        var (payload, payloadSource) = await BuildCollectionRequestPayload(description);
        if (payload.Parts.Count == 0)
        {
            await _tools.SendMessage(
                userId,
                "I couldn't build a valid engineering collection payload. Use `#collection` with inline JSON, or `#collection-file <path-to-json>`.",
                externalNotify: true);
            return null;
        }

        _logger.LogInformation(
            "Engineering collection payload source={Source}; parts={Parts}; connections={Connections}",
            payloadSource,
            payload.Parts.Count,
            payload.Connections?.Count ?? 0);

        var apiBaseUrl = Environment.GetEnvironmentVariable("DARCI_API_BASE_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            apiBaseUrl = "http://127.0.0.1:5080";
        }

        using var client = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(30)
        };

        using var response = await client.PostAsJsonAsync("/engineering/collection", payload);
        var body = await response.Content.ReadAsStringAsync();

        var summary = ParseCollectionResponse(body);
        var passed = response.IsSuccessStatusCode && summary.ValidationPassed && summary.SimulationPassed;

        var statusText = passed ? "complete" : "finished with issues";
        var lines = new List<string>
        {
            $"Engineering collection {statusText}.",
            $"  Payload source: {payloadSource}",
            $"  Payload: {payload.Parts.Count} parts, {payload.Connections?.Count ?? 0} connections",
            $"  Validation passed: {summary.ValidationPassed}",
            $"  Simulation passed: {summary.SimulationPassed}",
            $"  Output dir: {summary.OutputDir ?? "n/a"}"
        };
        if (!string.IsNullOrWhiteSpace(summary.ZipPath))
        {
            lines.Add($"  Zip: {summary.ZipPath}");
        }
        if (summary.IssuePreview.Count > 0)
        {
            lines.Add("  Issues:");
            lines.AddRange(summary.IssuePreview.Select(i => $"   - {i}"));
        }

        var message = string.Join("\n", lines);
        await _tools.SendMessage(userId, message, externalNotify: true);

        await _tools.StoreMemory(
            $"Engineering collection run for '{description}' ({payloadSource}) {statusText}. Output={summary.OutputDir}; zip={summary.ZipPath}; validation={summary.ValidationPassed}; simulation={summary.SimulationPassed}",
            new[] { "engineering", "collection", passed ? "success" : "failure", userId });

        return new
        {
            success = passed,
            outputDir = summary.OutputDir,
            zipPath = summary.ZipPath,
            payloadSource,
            validationPassed = summary.ValidationPassed,
            simulationPassed = summary.SimulationPassed
        };
    }

    private async Task<(EngineeringCollectionRequestPayload Payload, string Source)> BuildCollectionRequestPayload(string description)
    {
        var fromFile = TryParseCollectionPayloadFromFileDirective(description, out var filePath);
        if (fromFile != null)
        {
            var source = string.IsNullOrWhiteSpace(filePath)
                ? "file-directive"
                : $"file-directive:{filePath}";
            return (NormalizeCollectionPayload(fromFile, description), source);
        }

        var fromTaggedJson = TryParseCollectionPayload(description);
        if (fromTaggedJson != null)
        {
            return (NormalizeCollectionPayload(fromTaggedJson, description), "inline-json");
        }

        var portalTemplate = TryBuildPortalCollectionTemplate(description);
        if (portalTemplate != null)
        {
            return (portalTemplate, "portal-template");
        }

        var prompt = BuildCollectionPrompt(description);
        var llm = await _tools.Generate(prompt);
        var fromLlm = TryParseCollectionPayload(llm);
        if (fromLlm != null)
        {
            return (NormalizeCollectionPayload(fromLlm, description), "llm-json");
        }

        return (
            NormalizeCollectionPayload(new EngineeringCollectionRequestPayload
            {
                Name = "engineering-collection",
                Parts = new List<EngineeringCollectionPartPayload>
                {
                    new()
                    {
                        Name = "main-part",
                        Description = description
                    }
                }
            }, description),
            "fallback-single-part");
    }

    private static EngineeringCollectionRequestPayload? TryParseCollectionPayloadFromFileDirective(
        string raw,
        out string? resolvedPath)
    {
        resolvedPath = null;
        foreach (var candidate in ExtractCollectionFilePathCandidates(raw))
        {
            var path = ResolveCollectionFilePath(candidate);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var fileContent = File.ReadAllText(path);
                var payload = TryParseCollectionPayload(fileContent);
                if (payload != null)
                {
                    resolvedPath = path;
                    return payload;
                }
            }
            catch
            {
                // Keep scanning additional candidates.
            }
        }

        return null;
    }

    private static EngineeringCollectionRequestPayload? TryParseCollectionPayload(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EngineeringCollectionRequestPayload>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractCollectionFilePathCandidates(string raw)
    {
        var candidates = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return candidates;
        }

        var patterns = new[]
        {
            @"(?im)^\s*(?:#|/)?(?:collection|assembly)(?:-file|\s+file)\s*[:=]?\s*(?<path>.+?)\s*$",
            @"(?im)^\s*collection[_\-\s]?file\s*[:=]\s*(?<path>.+?)\s*$",
            @"(?im)^\s*assembly[_\-\s]?file\s*[:=]\s*(?<path>.+?)\s*$"
        };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(raw, pattern))
            {
                if (!match.Success)
                {
                    continue;
                }

                var path = NormalizePathToken(match.Groups["path"].Value);
                if (IsLikelyJsonPath(path))
                {
                    candidates.Add(path);
                }
            }
        }

        var trimmed = NormalizePathToken(raw.Trim());
        if (!raw.Contains('\n') && IsLikelyJsonPath(trimmed))
        {
            candidates.Add(trimmed);
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePathToken(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "";
        }

        var cleaned = rawPath.Trim().Trim('"', '\'', '`').Trim();
        if (cleaned.EndsWith(".", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^1];
        }

        return cleaned;
    }

    private static bool IsLikelyJsonPath(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        return candidatePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveCollectionFilePath(string candidatePath)
    {
        var normalized = NormalizePathToken(candidatePath);
        if (!IsLikelyJsonPath(normalized))
        {
            return null;
        }

        var possiblePaths = new List<string>();
        void AddCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                possiblePaths.Add(Path.GetFullPath(path));
            }
            catch
            {
                // Ignore malformed candidate paths.
            }
        }

        if (Path.IsPathRooted(normalized))
        {
            AddCandidate(normalized);
        }
        else
        {
            AddCandidate(Path.Combine(Directory.GetCurrentDirectory(), normalized));
            AddCandidate(Path.Combine(AppContext.BaseDirectory, normalized));
        }

        var darciRoot = TryFindDarciRoot();
        if (!string.IsNullOrWhiteSpace(darciRoot))
        {
            AddCandidate(Path.Combine(darciRoot, normalized));
            var parentRoot = Directory.GetParent(darciRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(parentRoot))
            {
                AddCandidate(Path.Combine(parentRoot, normalized));
            }

            var forward = normalized.Replace('\\', '/');
            const string darciPrefix = "darci-v3/";
            if (forward.StartsWith(darciPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var tail = forward[darciPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                AddCandidate(Path.Combine(darciRoot, tail));
            }
        }

        foreach (var candidate in possiblePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryFindDarciRoot()
    {
        static string? Scan(string startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                return null;
            }

            DirectoryInfo? current;
            try
            {
                current = new DirectoryInfo(startPath);
            }
            catch
            {
                return null;
            }

            while (current != null)
            {
                var hasApi = Directory.Exists(Path.Combine(current.FullName, "Darci.Api"));
                var hasSolution = File.Exists(Path.Combine(current.FullName, "DARCI.sln"));
                if (hasApi && hasSolution)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        return Scan(Directory.GetCurrentDirectory())
            ?? Scan(AppContext.BaseDirectory);
    }

    private static EngineeringCollectionRequestPayload NormalizeCollectionPayload(
        EngineeringCollectionRequestPayload payload,
        string fallbackDescription)
    {
        payload.Name = string.IsNullOrWhiteSpace(payload.Name) ? "engineering-collection" : payload.Name.Trim();
        payload.StrictValidation ??= true;
        payload.RunSimulation ??= true;
        payload.CreateZip ??= true;
        payload.DefaultMaxIterations ??= 3;
        payload.SimulationSamples ??= 256;
        payload.CollisionToleranceMm ??= 0.1;
        payload.ClearanceTargetMm ??= 0.2;
        payload.Connections ??= new List<EngineeringCollectionConnectionPayload>();

        payload.Parts ??= new List<EngineeringCollectionPartPayload>();
        for (var i = 0; i < payload.Parts.Count; i++)
        {
            var p = payload.Parts[i];
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? $"part-{i + 1}" : p.Name.Trim();
            p.Description = string.IsNullOrWhiteSpace(p.Description)
                ? fallbackDescription
                : p.Description.Trim();
            p.MaxIterations ??= payload.DefaultMaxIterations;
            payload.Parts[i] = p;
        }

        return payload;
    }

    private static EngineeringCollectionRequestPayload? TryBuildPortalCollectionTemplate(string description)
    {
        var lower = description.ToLowerInvariant();
        if (!lower.Contains("portal") || !lower.Contains("gear"))
        {
            return null;
        }

        var module = ExtractDouble(lower, @"module\s*([0-9]+(?:\.[0-9]+)?)") ?? 2.0;
        var driveTeeth = ExtractInt(lower, @"(?:drive|pinion)[^0-9]{0,16}(\d+)\s*(?:teeth|tooth)") ?? 12;
        var drivenTeeth = ExtractInt(lower, @"(?:driven|output|idler)[^0-9]{0,16}(\d+)\s*(?:teeth|tooth)") ?? 24;

        return new EngineeringCollectionRequestPayload
        {
            Name = "portal-gear-assembly",
            StrictValidation = true,
            RunSimulation = true,
            CreateZip = true,
            DefaultMaxIterations = 4,
            Parts = new List<EngineeringCollectionPartPayload>
            {
                new()
                {
                    Name = "drive_gear",
                    Description = "Drive gear for portal assembly",
                    PartType = "gear",
                    Parameters = new Dictionary<string, double>
                    {
                        ["teeth"] = driveTeeth,
                        ["module"] = module,
                        ["bore_diameter_mm"] = 8.0,
                        ["face_width_mm"] = 10.0
                    },
                    X = 0,
                    Y = 0,
                    Z = 0
                },
                new()
                {
                    Name = "driven_gear",
                    Description = "Driven gear for portal assembly",
                    PartType = "gear",
                    Parameters = new Dictionary<string, double>
                    {
                        ["teeth"] = drivenTeeth,
                        ["module"] = module,
                        ["bore_diameter_mm"] = 10.0,
                        ["face_width_mm"] = 10.0
                    },
                    X = module * (driveTeeth + drivenTeeth) * 0.5,
                    Y = 0,
                    Z = 0
                },
                new()
                {
                    Name = "gear_housing",
                    Description = "Housing body for portal gear pair",
                    PartType = "housing",
                    Parameters = new Dictionary<string, double>
                    {
                        ["length_mm"] = 120.0,
                        ["width_mm"] = 80.0,
                        ["height_mm"] = 60.0,
                        ["center_bore_mm"] = 26.0
                    },
                    X = (module * (driveTeeth + drivenTeeth) * 0.5) / 2.0,
                    Y = -10.0,
                    Z = -5.0
                }
            },
            Connections = new List<EngineeringCollectionConnectionPayload>
            {
                new()
                {
                    From = "drive_gear",
                    To = "driven_gear",
                    Relation = "mesh",
                    Motion = new EngineeringConnectionMotionPayload
                    {
                        Type = "rotational",
                        Axis = new List<double> { 0, 0, 1 },
                        RangeDeg = 180,
                        Steps = 13,
                        MovingPart = "driven_gear"
                    }
                },
                new()
                {
                    From = "gear_housing",
                    To = "drive_gear",
                    Relation = "houses"
                },
                new()
                {
                    From = "gear_housing",
                    To = "driven_gear",
                    Relation = "houses"
                }
            }
        };
    }

    private static string BuildCollectionPrompt(string description)
    {
        return $@"Convert this engineering request into JSON for DARCI's engineering collection pipeline.
Return ONLY valid JSON. No markdown.

Schema:
{{
  ""name"": ""string"",
  ""parts"": [
    {{
      ""name"": ""string"",
      ""description"": ""string"",
      ""partType"": ""gear|shaft|bearing|pin|housing|plate|bracket|other"",
      ""parameters"": {{ ""key"": number }},
      ""x"": number,
      ""y"": number,
      ""z"": number
    }}
  ],
  ""connections"": [
    {{
      ""from"": ""part-name"",
      ""to"": ""part-name"",
      ""relation"": ""mesh|mates|houses|retained|connects"",
      ""motion"": {{
        ""type"": ""rotational|linear"",
        ""axis"": [x, y, z],
        ""rangeDeg"": number,
        ""rangeMm"": number,
        ""steps"": number,
        ""movingPart"": ""part-name""
      }}
    }}
  ],
  ""strictValidation"": true,
  ""runSimulation"": true,
  ""createZip"": true
}}

Request:
{description}";
    }

    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Contains("```json", StringComparison.OrdinalIgnoreCase))
        {
            var start = trimmed.IndexOf("```json", StringComparison.OrdinalIgnoreCase) + "```json".Length;
            var end = trimmed.IndexOf("```", start, StringComparison.Ordinal);
            if (end > start)
            {
                trimmed = trimmed[start..end].Trim();
            }
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start)
            {
                trimmed = trimmed[(start + 1)..end].Trim();
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            return null;
        }

        return trimmed[firstBrace..(lastBrace + 1)];
    }

    private static int? ExtractInt(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!match.Success || match.Groups.Count < 2)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static double? ExtractDouble(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (!match.Success || match.Groups.Count < 2)
        {
            return null;
        }

        return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static CollectionRunSummary ParseCollectionResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new CollectionRunSummary();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var collection = root.TryGetProperty("collection", out var collectionEl) ? collectionEl : root;
            var validation = collection.TryGetProperty("validation", out var valEl) ? valEl : default;
            var simulation = root.TryGetProperty("simulation", out var simEl)
                ? simEl
                : (collection.TryGetProperty("simulation", out var nestedSimEl) ? nestedSimEl : default);

            var summary = new CollectionRunSummary
            {
                OutputDir = collection.TryGetProperty("outputDir", out var outEl) ? outEl.GetString() : null,
                ZipPath = collection.TryGetProperty("zipPath", out var zipEl) ? zipEl.GetString() : null,
                ValidationPassed = validation.ValueKind == JsonValueKind.Object
                    && validation.TryGetProperty("passed", out var passedEl)
                    && passedEl.GetBoolean(),
                SimulationPassed = simulation.ValueKind != JsonValueKind.Object
                    || (simulation.TryGetProperty("passed", out var simPassedEl) && simPassedEl.GetBoolean())
            };

            if (validation.ValueKind == JsonValueKind.Object
                && validation.TryGetProperty("issues", out var issuesEl)
                && issuesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in issuesEl.EnumerateArray().Take(3))
                {
                    if (issue.TryGetProperty("message", out var msgEl))
                    {
                        var msg = msgEl.GetString();
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            summary.IssuePreview.Add(msg.Trim());
                        }
                    }
                }
            }

            if (summary.IssuePreview.Count < 3
                && simulation.ValueKind == JsonValueKind.Object
                && simulation.TryGetProperty("issues", out var simIssuesEl)
                && simIssuesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in simIssuesEl.EnumerateArray().Take(3 - summary.IssuePreview.Count))
                {
                    if (issue.TryGetProperty("message", out var msgEl))
                    {
                        var msg = msgEl.GetString();
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            summary.IssuePreview.Add(msg.Trim());
                        }
                    }
                }
            }

            return summary;
        }
        catch
        {
            return new CollectionRunSummary();
        }
    }
    
    // ========== Status ==========
    
    public DarciStatus GetStatus() => new()
    {
        IsAlive = _uptime.IsRunning,
        Uptime = _uptime.Elapsed,
        CycleCount = _cycleCount,
        CurrentMood = _state.CurrentMood.ToString(),
        Energy = _state.Energy,
        CurrentActivity = _state.CurrentActivity
    };

    private static string BuildGoalStepProgressNote(DarciAction action)
    {
        var actionLabel = action.Type switch
        {
            ActionType.Research => "Completed research step.",
            ActionType.Think => "Completed generation/planning step.",
            ActionType.Notify => "Completed notification step.",
            ActionType.GenerateCAD => "Completed CAD generation step.",
            ActionType.Engineer => "Completed engineering validation step.",
            _ => "Completed goal step."
        };

        return string.IsNullOrWhiteSpace(action.Reasoning)
            ? actionLabel
            : $"{actionLabel} {action.Reasoning}";
    }

    private sealed class CollectionRunSummary
    {
        public string? OutputDir { get; init; }
        public string? ZipPath { get; init; }
        public bool ValidationPassed { get; init; }
        public bool SimulationPassed { get; init; } = true;
        public List<string> IssuePreview { get; } = new();
    }

    private sealed class EngineeringCollectionRequestPayload
    {
        public string Name { get; set; } = "engineering-collection";
        public List<EngineeringCollectionPartPayload> Parts { get; set; } = new();
        public List<EngineeringCollectionConnectionPayload>? Connections { get; set; }
        public int? DefaultMaxIterations { get; set; }
        public bool? StrictValidation { get; set; }
        public bool? RunSimulation { get; set; }
        public double? CollisionToleranceMm { get; set; }
        public double? ClearanceTargetMm { get; set; }
        public int? SimulationSamples { get; set; }
        public bool? CreateZip { get; set; }
    }

    private sealed class EngineeringCollectionPartPayload
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string? PartType { get; set; }
        public Dictionary<string, double>? Parameters { get; set; }
        public int? MaxIterations { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public double? RxDeg { get; set; }
        public double? RyDeg { get; set; }
        public double? RzDeg { get; set; }
    }

    private sealed class EngineeringCollectionConnectionPayload
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string? Relation { get; set; }
        public EngineeringConnectionMotionPayload? Motion { get; set; }
    }

    private sealed class EngineeringConnectionMotionPayload
    {
        public string? Type { get; set; }
        public List<double>? Axis { get; set; }
        public double? RangeDeg { get; set; }
        public double? RangeMm { get; set; }
        public int? Steps { get; set; }
        public List<double>? PivotMm { get; set; }
        public string? MovingPart { get; set; }
    }
}

public class DarciStatus
{
    public bool IsAlive { get; init; }
    public TimeSpan Uptime { get; init; }
    public long CycleCount { get; init; }
    public string CurrentMood { get; init; } = "";
    public float Energy { get; init; }
    public string? CurrentActivity { get; init; }
}
