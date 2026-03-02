using Darci.Brain;
using Darci.Shared;
using Darci.Tools;
using Darci.Goals;
using Microsoft.Extensions.Logging;

namespace Darci.Core;

/// <summary>
/// DARCI's decision-making system.
///
/// v4 changes (Phase 1 — instrumentation):
///   After every call to <see cref="Decide"/>, the chosen action and the
///   current state vector are logged to the <see cref="ExperienceBuffer"/>
///   decision_log table. This data drives behavioral cloning in Phase 2.
///
///   The v3 priority-ladder logic is preserved verbatim so the network
///   has a high-quality teacher signal. Once the trained network is loaded
///   (Phase 3), a future update will replace the ladder with a
///   <see cref="IDecisionNetwork.SelectAction"/> call.
/// </summary>
public class Decision
{
    private readonly ILogger<Decision> _logger;
    private readonly IToolkit _tools;
    private readonly IGoalManager _goals;
    private readonly IStateEncoder _encoder;
    private readonly ExperienceBuffer _buffer;

    public Decision(
        ILogger<Decision> logger,
        IToolkit tools,
        IGoalManager goals,
        IStateEncoder encoder,
        ExperienceBuffer buffer)
    {
        _logger = logger;
        _tools = tools;
        _goals = goals;
        _encoder = encoder;
        _buffer = buffer;
    }

    /// <summary>
    /// Given DARCI's current state and what she perceives, decide what to do.
    /// Logs the (state_vector, action_id) pair to the experience buffer.
    /// </summary>
    public async Task<DarciAction> Decide(State state, Perception perception)
    {
        // Encode the state BEFORE running the priority ladder so the vector
        // reflects the moment of decision, not the moment after acting.
        var stateVector = _encoder.Encode(state.ToEncoderInput(perception));

        var action = await RunPriorityLadder(state, perception);

        // Log for behavioral cloning (Phase 2 training data).
        _ = LogDecisionAsync(stateVector, action);

        return action;
    }

    // =========================================================
    // v3 Priority Ladder (preserved verbatim as the teacher)
    // =========================================================

    private async Task<DarciAction> RunPriorityLadder(State state, Perception perception)
    {
        // Priority 1: Urgent messages always get attention
        var urgentMessage = perception.NewMessages
            .FirstOrDefault(m => m.Urgency >= Urgency.Now);

        if (urgentMessage != null)
        {
            _logger.LogDebug("Handling urgent message from {UserId}", urgentMessage.UserId);
            return await DecideAndMarkProcessed(urgentMessage, state);
        }

        // Priority 2: Normal messages
        var normalMessages = perception.NewMessages
            .Where(m => m.Urgency < Urgency.Now && !m.IsProcessed)
            .ToList();

        if (normalMessages.Any())
        {
            var message = normalMessages.First();
            _logger.LogDebug("Handling message from {UserId}", message.UserId);
            return await DecideAndMarkProcessed(message, state);
        }

        // Priority 3: Task completions need acknowledgment/next steps
        if (perception.CompletedTasks.Any())
        {
            var completion = perception.CompletedTasks.First();
            return await HandleTaskCompletion(completion, state);
        }

        // Priority 4: Goal events (due dates, blockers)
        var urgentGoalEvent = perception.GoalEvents
            .FirstOrDefault(e => e.Type == GoalEventType.DueSoon || e.Type == GoalEventType.Blocked);

        if (urgentGoalEvent != null)
        {
            return await HandleGoalEvent(urgentGoalEvent, state);
        }

        // Priority 5: Work on active goals
        if (state.CurrentGoalId.HasValue)
        {
            return await ContinueGoalWork(state.CurrentGoalId.Value, state);
        }

        // Priority 6: Pick up a goal to work on
        var nextGoal = await _goals.GetNextActionableGoal();
        if (nextGoal != null && state.Energy > 0.4f)
        {
            _logger.LogDebug("Picking up goal: {GoalId} - {Title}", nextGoal.Id, nextGoal.Title);
            state.StartActivity($"Working on: {nextGoal.Title}", nextGoal.Id);
            return DarciAction.WorkOn(nextGoal.Id, "This goal needs attention and I have energy for it");
        }

        // Priority 7: Memory maintenance
        if (perception.PendingMemoriesToProcess > 5 && state.Energy > 0.3f)
        {
            return new DarciAction
            {
                Type = ActionType.Consolidate,
                Reasoning = "Some memories need organizing"
            };
        }

        // Priority 8: Self-initiated thinking (rare, when truly idle)
        if (perception.TimeSinceLastAction > TimeSpan.FromMinutes(5) && state.Energy > 0.5f)
        {
            var topic = await ChooseThinkingTopic(state);
            if (topic != null)
            {
                return DarciAction.Think(topic, "I have time to reflect");
            }
        }

        // Default: Rest
        var restDuration = CalculateRestDuration(state, perception);
        return DarciAction.Rest(restDuration, "Nothing needs my attention right now");
    }

    // =========================================================
    // Decision logging (Phase 1 instrumentation)
    // =========================================================

    private async Task LogDecisionAsync(float[] stateVector, DarciAction action)
    {
        try
        {
            var log = new DecisionLog
            {
                StateVector     = stateVector,
                ActionChosen    = ActionTypeToBrainAction(action.Type),
                NetworkDecision = false,   // v3 ladder — not the network
                Confidence      = null,
                Timestamp       = DateTime.UtcNow
            };

            await _buffer.LogDecisionAsync(log);
        }
        catch (Exception ex)
        {
            // Logging must never crash the living loop.
            _logger.LogWarning(ex, "Failed to log decision to experience buffer");
        }
    }

    /// <summary>
    /// Maps a v3 ActionType to the closest BrainAction integer.
    /// Complex multi-step actions (Engineer, CAD, etc.) map to the
    /// dominant intent so the network can learn the pattern.
    /// </summary>
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
        // CAD/Engineering both map to WorkOnGoal — they're goal execution actions
        ActionType.GenerateCAD
            or ActionType.Engineer
            or ActionType.ReadFile
            or ActionType.WriteFile => (int)BrainAction.WorkOnGoal,
        _                      => (int)BrainAction.Think
    };

    // =========================================================
    // Response routing (unchanged from v3)
    // =========================================================

    private async Task<DarciAction> DecideResponseTo(IncomingMessage message, State state)
    {
        var intent = message.Intent ?? new MessageIntent { Type = IntentType.Unknown };

        return intent.Type switch
        {
            IntentType.Conversation or IntentType.Question or IntentType.StatusCheck =>
                await GenerateReplyAction(message, state),
            IntentType.Research  => await HandleResearchRequest(message, state),
            IntentType.Reminder  => await HandleReminderRequest(message, state),
            IntentType.Task      => await HandleTaskRequest(message, state),
            IntentType.CAD       => await HandleCADRequest(message, state),
            IntentType.EngineeringCollection => await HandleEngineeringCollectionRequest(message, state),
            IntentType.Feedback  => await HandleFeedback(message, state),
            IntentType.GoalUpdate => await HandleGoalUpdate(message, state),
            IntentType.Unknown   => await HandleUnknownIntent(message, state),
            _ => await GenerateReplyAction(message, state)
        };
    }

    private async Task<DarciAction> DecideAndMarkProcessed(IncomingMessage message, State state)
    {
        var action = await DecideResponseTo(message, state);
        message.IsProcessed = true;
        return action;
    }

    private static bool ShouldExternallyNotifyReply(IncomingMessage message)
        => string.Equals(message.Source, "telegram", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRunCadImmediately(IncomingMessage message)
        => message.Urgency >= Urgency.Now
        || string.Equals(message.Source, "telegram", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRunEngineeringCollectionImmediately(IncomingMessage message)
        => message.Urgency >= Urgency.Now
        || string.Equals(message.Source, "telegram", StringComparison.OrdinalIgnoreCase);

    private async Task<DarciAction> GenerateReplyAction(IncomingMessage message, State state)
    {
        var context = new ReplyContext
        {
            UserMessage = message.Content,
            UserId      = message.UserId,
            DarciState  = state.Describe(),
            Intent      = message.Intent
        };

        var memories = await _tools.RecallMemories(message.Content, limit: 5);
        context.RelevantMemories = memories;

        var activeGoals = await _goals.GetActiveGoals(message.UserId);
        context.ActiveGoals = activeGoals.Select(g => g.Title).ToList();

        var reply = await _tools.GenerateReply(context);

        return DarciAction.Reply(
            reply,
            message.UserId,
            message.Id,
            "Responding to user message",
            externalNotify: ShouldExternallyNotifyReply(message));
    }

    private async Task<DarciAction> HandleResearchRequest(IncomingMessage message, State state)
    {
        var topic = message.Intent?.ExtractedTopic ?? message.Content;

        await _goals.CreateGoal(new GoalCreation
        {
            Title    = $"Research: {TruncateForTitle(topic)}",
            Description = $"Research requested by user: {topic}",
            UserId   = message.UserId,
            Source   = GoalSource.UserRequested,
            Priority = message.Urgency == Urgency.Now ? GoalPriority.High : GoalPriority.Medium
        });

        var ack = message.Urgency >= Urgency.Now
            ? $"On it - researching {TruncateForTitle(topic)} now."
            : $"Got it - I'll look into {TruncateForTitle(topic)}.";

        return DarciAction.Reply(
            ack,
            message.UserId,
            message.Id,
            "Acknowledging research request",
            externalNotify: ShouldExternallyNotifyReply(message));
    }

    private async Task<DarciAction> HandleReminderRequest(IncomingMessage message, State state)
    {
        var reminderContent = message.Intent?.ExtractedTopic ?? message.Content;

        await _goals.CreateGoal(new GoalCreation
        {
            Title    = $"Reminder: {TruncateForTitle(reminderContent)}",
            Description = reminderContent,
            UserId   = message.UserId,
            Source   = GoalSource.UserRequested,
            Type     = GoalType.Reminder,
            Priority = GoalPriority.Medium
        });

        return DarciAction.Reply(
            $"I'll remember that. I've noted: {TruncateForTitle(reminderContent)}",
            message.UserId,
            message.Id,
            "Confirming reminder",
            externalNotify: ShouldExternallyNotifyReply(message));
    }

    private async Task<DarciAction> HandleTaskRequest(IncomingMessage message, State state)
    {
        await _goals.CreateGoal(new GoalCreation
        {
            Title       = TruncateForTitle(message.Content),
            Description = message.Content,
            UserId      = message.UserId,
            Source      = GoalSource.UserRequested,
            Priority    = message.Urgency >= Urgency.Now ? GoalPriority.High : GoalPriority.Medium
        });

        return DarciAction.Reply(
            "I understand. I'll work on this.",
            message.UserId,
            message.Id,
            "Acknowledging task request",
            externalNotify: ShouldExternallyNotifyReply(message));
    }

    private async Task<DarciAction> HandleCADRequest(IncomingMessage message, State state)
    {
        var description = message.Intent?.ExtractedTopic ?? message.Content;

        var goal = await _goals.CreateGoal(new GoalCreation
        {
            Title    = $"CAD: {TruncateForTitle(description)}",
            Description = description,
            UserId   = message.UserId,
            Source   = GoalSource.UserRequested,
            Type     = GoalType.CAD,
            Priority = message.Urgency >= Urgency.Now ? GoalPriority.High : GoalPriority.Medium
        });

        if (ShouldRunCadImmediately(message))
        {
            _logger.LogInformation("Immediate CAD generation for: {Desc}", description);
            return DarciAction.GenerateCad(
                description,
                userId:    message.UserId,
                messageId: message.Id,
                forGoalId: goal.Id,
                reason: "Urgent CAD request — generating now");
        }

        return DarciAction.Reply(
            $"Got it — I'll generate a 3D model for: {TruncateForTitle(description)}. I'll let you know when it's ready.",
            message.UserId,
            message.Id,
            "Acknowledging CAD request",
            externalNotify: ShouldExternallyNotifyReply(message));
    }

    private async Task<DarciAction> HandleEngineeringCollectionRequest(IncomingMessage message, State state)
    {
        var description = message.Intent?.ExtractedTopic ?? message.Content;

        if (ShouldRunEngineeringCollectionImmediately(message))
        {
            _logger.LogInformation("Immediate engineering collection run for: {Desc}", description);
            return DarciAction.EngineerCollection(
                description,
                userId:    message.UserId,
                messageId: message.Id,
                reason: "Engineering collection request from user");
        }

        return DarciAction.Reply(
            "I can run that as an engineering collection. Send it with `#collection` (optionally with JSON), or mark it urgent and I'll start immediately.",
            message.UserId,
            message.Id,
            "Prompting for collection execution mode",
            externalNotify: ShouldExternallyNotifyReply(message));
    }

    private async Task<DarciAction> HandleFeedback(IncomingMessage message, State state)
    {
        var lower = message.Content.ToLowerInvariant();
        var isPositive = ContainsAny(lower, "thank", "great", "good", "helpful", "perfect", "awesome");

        await _tools.StoreMemory(
            $"User feedback: {message.Content}",
            new[] { "feedback", isPositive ? "positive" : "negative" });

        return await GenerateReplyAction(message, state);
    }

    private async Task<DarciAction> HandleGoalUpdate(IncomingMessage message, State state)
        => await GenerateReplyAction(message, state);

    private async Task<DarciAction> HandleUnknownIntent(IncomingMessage message, State state)
    {
        var classifiedIntent = await _tools.ClassifyIntent(message.Content);
        message.Intent = classifiedIntent;
        return await DecideResponseTo(message, state);
    }

    private async Task<DarciAction> HandleTaskCompletion(TaskCompletion completion, State state)
    {
        if (completion.Success)
        {
            if (completion.TaskType == "research" && completion.Result is string)
            {
                return new DarciAction
                {
                    Type = ActionType.Notify,
                    MessageContent = "I finished looking into that. Here's what I found...",
                    RecipientId = "Tinman",
                    Reasoning = "Research task completed, user might want to know"
                };
            }

            if (completion.TaskType == "cad")
            {
                return DarciAction.Rest(TimeSpan.FromMilliseconds(100), "CAD task completed and user notified");
            }
        }

        return DarciAction.Rest(TimeSpan.FromMilliseconds(100), "Processed task completion");
    }

    private async Task<DarciAction> HandleGoalEvent(GoalEvent evt, State state)
    {
        var goal = await _goals.GetGoal(evt.GoalId);
        if (goal == null) return DarciAction.Rest(TimeSpan.FromMilliseconds(100));

        return evt.Type switch
        {
            GoalEventType.DueSoon => new DarciAction
            {
                Type = ActionType.Notify,
                MessageContent = $"Heads up - '{goal.Title}' is due soon.",
                RecipientId = goal.UserId,
                InResponseToGoalId = goal.Id,
                Reasoning = "Goal is approaching deadline"
            },
            GoalEventType.Blocked => new DarciAction
            {
                Type = ActionType.Think,
                Topic = $"How to unblock goal: {goal.Title}",
                InResponseToGoalId = goal.Id,
                Reasoning = "Need to figure out how to proceed"
            },
            _ => DarciAction.Rest(TimeSpan.FromMilliseconds(100))
        };
    }

    private async Task<DarciAction> ContinueGoalWork(int goalId, State state)
    {
        var goal = await _goals.GetGoal(goalId);
        if (goal == null)
        {
            state.EndActivity();
            return DarciAction.Rest(TimeSpan.FromMilliseconds(100), "Goal no longer exists");
        }

        var nextStep = await _goals.GetNextStep(goalId);

        if (nextStep == null)
        {
            state.EndActivity();
            return DarciAction.Rest(TimeSpan.FromMilliseconds(100), "No more steps for this goal");
        }

        return nextStep.Type switch
        {
            GoalStepType.Research => DarciAction.Research(
                nextStep.Query!,
                goalId,
                "Working on goal research",
                progressGoalStepOnSuccess: true),
            GoalStepType.Generate => new DarciAction
            {
                Type = ActionType.Think,
                Prompt = nextStep.Prompt,
                InResponseToGoalId = goalId,
                ProgressGoalStepOnSuccess = true,
                Reasoning = "Generating content for goal"
            },
            GoalStepType.Notify => new DarciAction
            {
                Type = ActionType.Notify,
                MessageContent = nextStep.Message,
                RecipientId = goal.UserId,
                InResponseToGoalId = goalId,
                ProgressGoalStepOnSuccess = true,
                Reasoning = "Goal step requires user notification"
            },
            GoalStepType.CAD => DarciAction.GenerateCad(
                goal.Description,
                userId:    goal.UserId,
                forGoalId: goalId,
                progressGoalStepOnSuccess: true,
                reason: "Working on CAD goal step"),
            GoalStepType.Engineer => DarciAction.Engineer(
                goal.Description,
                userId:    goal.UserId,
                forGoalId: goalId,
                progressGoalStepOnSuccess: true,
                reason: "Running engineering workbench step"),
            _ => DarciAction.Rest(TimeSpan.FromMilliseconds(100))
        };
    }

    private async Task<string?> ChooseThinkingTopic(State state)
    {
        var topics = new[]
        {
            "patterns in recent conversations",
            "how I can be more helpful",
            "what I've learned recently"
        };

        if (state.ConsecutiveRestCycles > 100)
        {
            return topics[Random.Shared.Next(topics.Length)];
        }

        return null;
    }

    private TimeSpan CalculateRestDuration(State state, Perception perception)
    {
        var baseRest = TimeSpan.FromMilliseconds(500);

        if (perception.TimeSinceLastUserContact < TimeSpan.FromMinutes(5))
            return TimeSpan.FromMilliseconds(100);

        if (state.Energy > 0.7f)
            return TimeSpan.FromMilliseconds(200);

        if (perception.IsQuietHours)
            return TimeSpan.FromSeconds(5);

        var multiplier = Math.Min(state.ConsecutiveRestCycles / 10.0, 10.0);
        return TimeSpan.FromMilliseconds(baseRest.TotalMilliseconds * (1 + multiplier));
    }

    private string TruncateForTitle(string text)
    {
        var cleaned = text.Trim();
        return cleaned.Length > 50 ? cleaned[..47] + "..." : cleaned;
    }

    private bool ContainsAny(string text, params string[] patterns)
        => patterns.Any(p => text.Contains(p));
}
