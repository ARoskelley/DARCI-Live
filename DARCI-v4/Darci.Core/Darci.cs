using Darci.Brain;
using Darci.Engineering;
using Darci.Goals;
using Darci.Research.Agents;
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
/// Her living loop: Perceive → Feel → Decide → Act → Reflect
///
/// v4 changes (Phase 1 — instrumentation):
///   Each cycle now also:
///     1. Encodes state as a 29-dim vector before deciding
///     2. Computes a reward signal after the action completes
///     3. Stores the (prev_state, action, reward, next_state) experience
///        in the SQLite ring buffer for DQN training
///   No behaviour changes — the priority ladder still drives decisions.
/// </summary>
public class Darci : BackgroundService
{
    private readonly ILogger<Darci> _logger;
    private readonly Awareness _awareness;
    private readonly Decision _decision;
    private readonly State _state;
    private readonly IToolkit _tools;
    private readonly IStateEncoder _encoder;
    private readonly ExperienceBuffer _buffer;
    private readonly EngineeringOrchestrator? _engineeringOrchestrator;
    private readonly IDeepResearchOrchestrator? _research;
    private readonly ConstraintExtractor? _constraintExtractor;
    private readonly IAutonomousBundler? _bundler;
    private readonly BomGenerator? _bomGenerator;
    private readonly IGoalManager? _goalManager;

    private long _cycleCount = 0;
    private readonly Stopwatch _uptime = new();

    // Carry experience data across cycles (DQN requires next-state from next cycle)
    private float[]? _prevStateVector;
    private int _prevActionId;
    private float _prevReward;
    private bool _prevHasData;

    // Rate-limit tracking for the spam-prevention reward penalty
    private DateTime _lastReplyAt = DateTime.MinValue;

    public Darci(
        ILogger<Darci> logger,
        Awareness awareness,
        Decision decision,
        State state,
        IToolkit tools,
        IStateEncoder encoder,
        ExperienceBuffer buffer,
        EngineeringOrchestrator? engineeringOrchestrator = null,
        IDeepResearchOrchestrator? research = null,
        ConstraintExtractor? constraintExtractor = null,
        IAutonomousBundler? bundler = null,
        BomGenerator? bomGenerator = null,
        IGoalManager? goalManager = null)
    {
        _logger                   = logger;
        _awareness                = awareness;
        _decision                 = decision;
        _state                    = state;
        _tools                    = tools;
        _encoder                  = encoder;
        _buffer                   = buffer;
        _engineeringOrchestrator  = engineeringOrchestrator;
        _research                 = research;
        _constraintExtractor      = constraintExtractor;
        _bundler                  = bundler;
        _bomGenerator             = bomGenerator;
        _goalManager              = goalManager;
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
║     Version 4.0 - Neural Decision Network                     ║
║                                                               ║
╚═══════════════════════════════════════════════════════════════╝
");
        _logger.LogInformation("DARCI is waking up...");

        try
        {
            await _state.Initialize();
            await _buffer.InitializeAsync();

            _uptime.Start();
            _logger.LogInformation("DARCI is alive. Experience buffer ready.");

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
    /// Perceive → Feel → Decide → Act → Reflect → Record
    /// </summary>
    private async Task Live(CancellationToken ct)
    {
        _cycleCount++;

        try
        {
            // 1. PERCEIVE
            var perception = await _awareness.Perceive();

            // 2. FEEL
            await _state.React(perception);

            // 3. Encode the current state BEFORE deciding.
            //    This becomes both the "next_state" for last cycle's experience
            //    and the "state" for this cycle's experience.
            var currentVector = _encoder.Encode(_state.ToEncoderInput(perception));

            // 3a. If we have a pending experience from the previous cycle,
            //     complete it now that we have the next-state vector.
            if (_prevHasData && _prevStateVector is not null)
            {
                _ = StoreExperienceAsync(_prevStateVector, _prevActionId, _prevReward, currentVector);
            }

            // 4. DECIDE
            var action = await _decision.Decide(_state, perception);

            // 5. ACT
            var outcome = await Act(action, ct);

            // 6. REFLECT
            await _state.Process(outcome);

            if (action.Type != ActionType.Rest)
            {
                _awareness.RecordAction();
            }

            // 7. REWARD — score this action/outcome for the RL buffer
            var reward = ComputeReward(action, outcome, perception);

            // Stash for next cycle (we need next cycle's state vector to complete the tuple)
            _prevStateVector = currentVector;
            _prevActionId    = ActionTypeToBrainAction(action.Type);
            _prevReward      = reward;
            _prevHasData     = true;

            if (reward != 0f)
            {
                _logger.LogDebug("Reward: {Reward:+0.0;-0.0} for action {Action}", reward, action.Type);
            }

            // If resting, wait efficiently
            if (action.Type == ActionType.Rest)
            {
                await _awareness.WaitForEventOrTimeout(action.RestDuration, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error in life cycle {Cycle}", _cycleCount);
            await Task.Delay(1000, ct);
        }
    }

    // =========================================================
    // Phase 1: Reward Signal (ARCHITECTURE.md §5.1)
    // =========================================================

    /// <summary>
    /// Calculates the immediate reward for the action just taken.
    /// Implements the reward table from ARCHITECTURE.md §5.1.
    /// Delayed rewards (thanks, repeated requests) are not handled here —
    /// they will be applied retroactively in Phase 2.
    /// </summary>
    private float ComputeReward(DarciAction action, Outcome outcome, Perception perception)
    {
        if (!outcome.Success)
        {
            // Wasted a cycle (action failed)
            return -0.3f;
        }

        switch (action.Type)
        {
            case ActionType.Rest:
            {
                // Penalise resting when there are messages to handle
                if (perception.NewMessages.Any(m => !m.IsProcessed))
                    return -1.0f;
                // Small reward for efficient idle behaviour
                return 0.1f;
            }

            case ActionType.Reply:
            {
                // Spam guard: reply within 30s of last reply = heavy penalty
                var now = DateTime.UtcNow;
                if ((now - _lastReplyAt).TotalSeconds < 30)
                {
                    _lastReplyAt = now;
                    return -2.0f;
                }
                _lastReplyAt = now;

                // Bonus for urgency
                var wasUrgent = perception.NewMessages
                    .Any(m => m.IsProcessed && m.Urgency >= Urgency.Now);
                return wasUrgent ? 1.5f : 1.0f;
            }

            case ActionType.Notify:
                return 0.8f;  // Proactively notified user about something important

            case ActionType.Research:
            {
                var reward = 0.4f;  // Meaningful work toward a goal or question
                if (outcome.Result is string researchResult && researchResult.Contains("[Confidence:", StringComparison.Ordinal))
                {
                    reward += 0.15f;
                    if (researchResult.Contains("UNCERTAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        reward += 0.05f;
                    }
                }

                return reward;
            }

            case ActionType.CreateGoal:
            case ActionType.ReviewGoals:
                // Reward only if it came from a clear request (not duplicate creation)
                return 0.8f;

            case ActionType.WorkOnGoal:
                // Advanced a goal step
                return outcome.GoalIdProgressed.HasValue ? 0.6f : 0.1f;

            case ActionType.Remember:
                return 0.3f;  // Built knowledge base

            case ActionType.Recall:
                return 0.4f;  // Leveraged past experience

            case ActionType.Consolidate:
                return 0.2f;  // Maintenance work

            case ActionType.Think:
            case ActionType.Decide:
                return 0.1f;  // Reflection has mild positive value

            case ActionType.GenerateCAD:
            case ActionType.Engineer:
                // These complete goal steps — treat as WorkOnGoal
                return outcome.GoalIdProgressed.HasValue ? 0.6f : 0.3f;

            case ActionType.ReadFile:
            case ActionType.WriteFile:
                return 0.2f;

            case ActionType.Observe:
                return 0.0f;

            default:
                return 0.0f;
        }
    }

    private async Task StoreExperienceAsync(
        float[] state, int actionId, float reward, float[] nextState)
    {
        try
        {
            await _buffer.StoreAsync(new Experience
            {
                State      = state,
                Action     = actionId,
                Reward     = reward,
                NextState  = nextState,
                IsTerminal = false,
                Timestamp  = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store experience in buffer");
        }
    }

    private static int ActionTypeToBrainAction(ActionType actionType) => actionType switch
    {
        ActionType.Rest        => (int)BrainAction.Rest,
        ActionType.Reply       => (int)BrainAction.ReplyToMessage,
        ActionType.Notify      => (int)BrainAction.NotifyUser,
        ActionType.Research    => (int)BrainAction.Research,
        ActionType.Think
            or ActionType.Decide
            or ActionType.Observe => (int)BrainAction.Think,
        ActionType.Remember    => (int)BrainAction.StoreMemory,
        ActionType.Recall      => (int)BrainAction.RecallMemories,
        ActionType.Consolidate => (int)BrainAction.ConsolidateMemories,
        ActionType.WorkOnGoal  => (int)BrainAction.WorkOnGoal,
        ActionType.CreateGoal
            or ActionType.ReviewGoals => (int)BrainAction.CreateGoal,
        ActionType.GenerateCAD
            or ActionType.Engineer
            or ActionType.ReadFile
            or ActionType.WriteFile => (int)BrainAction.WorkOnGoal,
        _                      => (int)BrainAction.Think
    };

    // =========================================================
    // Act() and action implementations (unchanged from v3)
    // =========================================================

    private async Task<Outcome> Act(DarciAction action, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var result = action.Type switch
            {
                ActionType.Reply      => await DoReply(action),
                ActionType.Notify     => await DoNotify(action),
                ActionType.Think      => await DoThink(action),
                ActionType.Remember   => await DoRemember(action),
                ActionType.Recall     => await DoRecall(action),
                ActionType.Consolidate => await DoConsolidate(action),
                ActionType.Research   => await DoResearch(action, ct),
                ActionType.WorkOnGoal => await DoGoalWork(action, ct),
                ActionType.CreateGoal => await DoCreateGoal(action),
                ActionType.ReadFile   => await DoReadFile(action),
                ActionType.WriteFile  => await DoWriteFile(action),
                ActionType.GenerateCAD  => await DoCADWork(action),
                ActionType.Engineer    => await DoEngineerWork(action),
                ActionType.Engineering => await DoNeuralEngineeringWork(action, ct),
                ActionType.Rest        => null,
                ActionType.Observe    => null,
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
                Success            = true,
                ActionTaken        = action.Type,
                Result             = result,
                Duration           = sw.Elapsed,
                MessageIdHandled   = action.InResponseToMessageId,
                GoalIdProgressed   = action.InResponseToGoalId
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Action {Action} failed", action.Type);
            return Outcome.Failed(action.Type, ex.Message);
        }
    }

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
        var prompt = action.Prompt
            ?? (action.Topic != null ? $"Think deeply about: {action.Topic}" : "Reflect on recent events");

        var thought = await _tools.Generate(prompt);

        if (thought.Length > 100)
        {
            await _tools.StoreMemory(
                $"Reflection: {thought}",
                new[] { "thought", "reflection" });
        }

        return thought;
    }

    private async Task<object?> DoRemember(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.MemoryContent)) return null;

        await _tools.StoreMemory(
            action.MemoryContent,
            action.Tags?.ToArray() ?? Array.Empty<string>());

        return action.MemoryContent;
    }

    private async Task<object?> DoRecall(DarciAction action)
    {
        var query = action.Query ?? action.Topic ?? "recent events";
        var memories = await _tools.RecallMemories(query, limit: 5);
        return memories;
    }

    private async Task<object?> DoConsolidate(DarciAction action)
    {
        await _tools.ConsolidateMemories();
        return null;
    }

    private async Task<object?> DoResearch(DarciAction action, CancellationToken ct)
    {
        var query = action.Query ?? action.Topic;
        if (string.IsNullOrEmpty(query)) return null;

        var userId = action.RecipientId ?? "Tinman";

        if (_research is not null)
        {
            var outcome = await _research.RunResearchAsync(
                query,
                userId: userId,
                mode: ResearchOrchestrationMode.LearnAndSynthesize,
                ct: ct);

            var prefix = outcome.IsUncertain
                ? $"[Confidence: {outcome.Confidence:P0} - UNCERTAIN] "
                : $"[Confidence: {outcome.Confidence:P0}] ";

            var result = prefix + outcome.FinalAnswer;
            await _tools.StoreMemory(
                $"Research on '{query}': {result}",
                new[] { "research", "deep_research" });
            return result;
        }

        // Fallback to toolkit when no orchestrator is wired
        var results = await _tools.DoDeepResearchAsync(query, userId, ct);
        await _tools.StoreMemory(
            $"Research on '{query}': {results}",
            new[] { "research", "deep_research" });
        return results;
    }

    private async Task<object?> DoGoalWork(DarciAction action, CancellationToken ct)
    {
        if (!action.GoalId.HasValue) return null;

        if (_research is not null)
        {
            // §E: use goal title for richer gap detection topic — avoids "current goal step" noise
            var topic = await GetGoalTitleAsync(action.GoalId)
                ?? _state.CurrentActivity
                ?? action.Reasoning
                ?? "current goal step";
            await DetectAndFillKnowledgeGapAsync(topic, ct);
            // Regardless of outcome, continue — gap fill is best-effort
        }

        _logger.LogInformation("Working on goal {GoalId}", action.GoalId);
        return "Working on goal";
    }

    /// <summary>
    /// Returns the title of a goal by id, or null on any failure.
    /// Single async DB read — non-blocking, non-fatal.
    /// </summary>
    private async Task<string?> GetGoalTitleAsync(int? goalId)
    {
        if (goalId is null || _goalManager is null) return null;
        try
        {
            var goal = await _goalManager.GetGoal(goalId.Value);
            return goal?.Title;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Called during goal work. Checks if DARCI has sufficient knowledge
    /// for the given topic. If not, runs agents in LearnOnly mode to fill
    /// the gap. Silent — no notification, no Ollama call.
    /// Returns true if knowledge was sufficient or successfully filled.
    /// </summary>
    private async Task<bool> DetectAndFillKnowledgeGapAsync(string topic, CancellationToken ct)
    {
        if (_research is null) return true;

        try
        {
            var outcome = await _research.RunResearchAsync(
                topic,
                userId: "DARCI",
                mode: ResearchOrchestrationMode.LearnOnly,
                ct: ct);

            if (outcome.IsSuccess)
                _logger.LogDebug(
                    "Knowledge gap filled: '{Topic}' (confidence {Conf:P0})",
                    topic, outcome.Confidence);
            else
                _logger.LogDebug("Knowledge gap fill failed for: '{Topic}'", topic);

            return outcome.IsSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gap detection failed for topic: {Topic}", topic);
            return false;
        }
    }

    private async Task<object?> DoCreateGoal(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.GoalDescription)) return null;

        var goalId = await _tools.CreateGoal(action.GoalDescription, action.RecipientId ?? "Tinman");
        return goalId;
    }

    private async Task<object?> DoReadFile(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.FilePath)) return null;
        return await _tools.ReadFile(action.FilePath);
    }

    private async Task<object?> DoWriteFile(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.FilePath) || string.IsNullOrEmpty(action.FileContent))
            return null;

        await _tools.WriteFile(action.FilePath, action.FileContent);
        return action.FilePath;
    }

    private async Task<object?> DoCADWork(DarciAction action)
    {
        if (string.IsNullOrEmpty(action.CadDescription)) return null;

        var userId = action.RecipientId ?? "Tinman";
        _logger.LogInformation("Starting CAD generation for {User}: {Desc}", userId, action.CadDescription);

        CadDimensionSpec? dims = null;
        if (action.CadLengthMm.HasValue || action.CadWidthMm.HasValue || action.CadHeightMm.HasValue)
        {
            dims = new CadDimensionSpec
            {
                LengthMm = action.CadLengthMm,
                WidthMm  = action.CadWidthMm,
                HeightMm = action.CadHeightMm
            };
        }

        var result = await _tools.GenerateCAD(action.CadDescription, dims, action.CadMaxIterations);

        if (result.Success)
        {
            await _tools.StoreMemory(
                $"Generated CAD model for '{action.CadDescription}': STL at {result.FinalStlPath}",
                new[] { "cad", "success", userId });

            var msg = $"Your 3D model is ready!\n  STL: {result.FinalStlPath}";
            await _tools.SendMessage(userId, msg, externalNotify: true);
            _logger.LogInformation("CAD generation succeeded for: {Desc}", action.CadDescription);
        }
        else
        {
            await _tools.StoreMemory(
                $"CAD generation FAILED for '{action.CadDescription}': {result.Error}",
                new[] { "cad", "failure", userId });

            await _tools.SendMessage(
                userId,
                $"I wasn't able to generate that model. Error: {result.Error}",
                externalNotify: false);

            _logger.LogWarning("CAD generation failed for: {Desc} — {Error}", action.CadDescription, result.Error);
        }

        return result;
    }

    private async Task<object?> DoEngineerWork(DarciAction action)
    {
        if (string.IsNullOrWhiteSpace(action.EngineeringDescription)) return null;

        var userId = action.RecipientId ?? "Tinman";

        if (action.EngineeringRunCollection)
        {
            // Collection workflows are orchestrated at the API layer; Core just logs.
            _logger.LogInformation("Engineering collection requested for {User}: {Desc}", userId, action.EngineeringDescription);
            await _tools.SendMessage(
                userId,
                "Engineering collection run started. I'll report back when complete.",
                externalNotify: true);
            return null;
        }

        _logger.LogInformation("Starting engineering workbench for {User}: {Desc}", userId, action.EngineeringDescription);

        var request = new EngineeringWorkRequest
        {
            Description   = action.EngineeringDescription,
            MaxIterations = action.EngineeringMaxIterations
        };

        var result = await _tools.RunEngineeringWorkbench(request);

        if (result.Success && result.CadResult?.Success == true)
        {
            await _tools.StoreMemory(
                $"Engineering workbench succeeded for '{action.EngineeringDescription}': STL at {result.CadResult.StlPath}",
                new[] { "engineering", "cad", "success", userId });

            await _tools.SendMessage(
                userId,
                $"Engineering step complete.\n  STL: {result.CadResult.StlPath}",
                externalNotify: true);
        }
        else
        {
            await _tools.StoreMemory(
                $"Engineering workbench failed for '{action.EngineeringDescription}': {result.Error}",
                new[] { "engineering", "cad", "failure", userId });

            await _tools.SendMessage(
                userId,
                $"Engineering step failed: {result.Error ?? "unknown error"}",
                externalNotify: false);
        }

        return result;
    }

    /// <summary>
    /// Handles ActionType.Engineering — runs the neural geometry workbench loop.
    /// §C: runs research + constraint extraction before workbench.
    /// §G: bundles output into a timestamped folder after completion.
    /// §H: writes a BOM markdown file into the bundle.
    /// Degrades gracefully if any optional service is not registered.
    /// </summary>
    private async Task<object?> DoNeuralEngineeringWork(DarciAction action, CancellationToken ct)
    {
        if (_engineeringOrchestrator == null)
        {
            _logger.LogWarning("Engineering action received but EngineeringOrchestrator is not configured");
            return null;
        }

        var spec   = action.EngineeringSpec;
        var userId = action.RecipientId ?? "Tinman";

        if (spec == null)
        {
            _logger.LogWarning("Engineering action has no EngineeringSpec — skipping");
            return null;
        }

        _logger.LogInformation("Neural engineering starting: {Description}", spec.Description);

        // §C — Research pass: fill knowledge gaps and extract engineering constraints
        Dictionary<string, object>? extractedConstraints = null;
        if (_research is not null)
        {
            var researchOutcome = await _research.RunResearchAsync(
                spec.Description,
                userId: "DARCI",
                mode: ResearchOrchestrationMode.LearnOnly,
                ct: ct);

            if (researchOutcome.IsSuccess && _constraintExtractor is not null)
            {
                extractedConstraints = await _constraintExtractor.ExtractAsync(
                    researchOutcome, spec.Description, ct);

                if (extractedConstraints.Count > 0)
                {
                    var merged = new Dictionary<string, object>(
                        spec.Constraints ?? new Dictionary<string, object>());
                    foreach (var (k, v) in extractedConstraints)
                        merged.TryAdd(k, v); // never overwrite explicit user constraints
                    spec = spec with { Constraints = merged };
                    _logger.LogInformation(
                        "Merged {Count} research-derived constraints into engineering spec",
                        extractedConstraints.Count);
                }
            }
        }

        var result = await _engineeringOrchestrator.RunAsync(spec, ct);

        // §G — Bundle output into a folder
        string? bundlePath = null;
        if (_bundler is not null && (result.Success || result.ExportedStlPath is not null))
        {
            try
            {
                bundlePath = await _bundler.CreateAsync(spec.Description, result, ct);
                if (bundlePath is not null)
                {
                    _logger.LogInformation("Engineering bundle created: {Path}", bundlePath);
                    await _tools.StoreMemory(
                        $"Engineering project bundle: {spec.Description} → {bundlePath}",
                        new[] { "engineering", "bundle", "output" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bundler failed — engineering result still stored");
            }
        }

        // §H — Generate and write BOM
        if (_bomGenerator is not null && bundlePath is not null)
        {
            try
            {
                var bomMarkdown = await _bomGenerator.GenerateBomAsync(
                    spec.Description, result, extractedConstraints, ct);
                var bomPath = Path.Combine(bundlePath, "bill_of_materials.md");
                await File.WriteAllTextAsync(bomPath, bomMarkdown, ct);
                _logger.LogInformation("BOM written to {Path}", bomPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BOM generation failed");
            }
        }

        if (result.Success)
        {
            await _tools.StoreMemory(
                $"Neural engineering succeeded for '{spec.Description}': score {result.FinalScore:F2}, {result.StepsTaken} steps",
                new[] { "engineering", "neural", "success", userId });

            var msg = $"Engineering complete.\n  Score: {result.FinalScore:P0}  Steps: {result.StepsTaken}  Passed: {result.ValidationPassed}";
            if (bundlePath is not null) msg += $"\n  Output: {bundlePath}";
            await _tools.SendMessage(userId, msg, externalNotify: true);
        }
        else
        {
            await _tools.StoreMemory(
                $"Neural engineering failed for '{spec.Description}': {result.ErrorMessage}",
                new[] { "engineering", "neural", "failure", userId });

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                await _tools.SendMessage(
                    userId,
                    $"Engineering task couldn't complete: {result.ErrorMessage}",
                    externalNotify: false);
            }
        }

        return result;
    }

    private static string BuildGoalStepProgressNote(DarciAction action) => action.Type switch
    {
        ActionType.Research   => $"Completed research: {action.Query}",
        ActionType.Reply      => "Sent required message",
        ActionType.Notify     => "Sent notification",
        ActionType.Think      => $"Completed thinking on: {action.Topic}",
        ActionType.GenerateCAD => "Generated CAD model",
        ActionType.Engineer   => "Completed engineering workbench step",
        _ => $"Completed {action.Type} step"
    };

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

    private static string TruncateForLog(string text)
    {
        var cleaned = text.Trim();
        return cleaned.Length > 60 ? cleaned[..57] + "..." : cleaned;
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
