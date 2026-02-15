using Darci.Shared;
using Darci.Tools;
using Darci.Tools.Cad;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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
                ActionType.Rest => null,
                ActionType.Observe => null,
                _ => null
            };
            
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
    
    private async Task<object?> DoGoalWork(DarciAction action)
    {
        if (!action.GoalId.HasValue)
            return null;
        
        await _tools.ProgressGoal(action.GoalId.Value);
        return true;
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
