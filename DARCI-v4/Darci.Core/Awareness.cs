using System.Threading.Channels;
using Darci.Shared;
using Darci.Memory;
using Darci.Goals;
using Darci.Tools;
using Lizzy.Client;
using Lizzy.Core.Models;
using Microsoft.Extensions.Logging;
using DarciIntentType  = Darci.Shared.IntentType;
using LizzyIntentType  = Lizzy.Core.Models.IntentType;

namespace Darci.Core;

/// <summary>
/// DARCI's perception system — aggregates everything she might notice:
/// messages, goal events, task completions, memory pressure.
///
/// v4 change: <see cref="Perceive"/> now also queries
/// <see cref="IGoalManager.GetGoalsWithPendingStepsCount"/> so that
/// state dimension [13] (goals_with_pending_steps) is populated.
/// </summary>
public class Awareness
{
    private readonly ILogger<Awareness> _logger;
    private readonly IMemoryStore _memory;
    private readonly IGoalManager _goals;
    private readonly Channel<IncomingMessage> _messageChannel;
    private readonly Channel<TaskCompletion> _taskCompletionChannel;
    private readonly IToolkit _toolkit;
    private readonly LizzyClient _lizzy;
    private readonly ExtractionSchema? _extractionSchema;
    private readonly List<IncomingMessage> _messageBacklog = new();
    private readonly List<ProcessedMessage> _processedBacklog = new();
    private DateTime _lastUserContact = DateTime.UtcNow;
    private DateTime _lastAction = DateTime.UtcNow;

    private readonly TimeOnly _quietStart = new(0, 0);
    private readonly TimeOnly _quietEnd = new(6, 0);

    public Awareness(
        ILogger<Awareness> logger,
        IMemoryStore memory,
        IGoalManager goals,
        IToolkit toolkit,
        LizzyClient lizzy)
    {
        _logger = logger;
        _memory = memory;
        _goals = goals;
        _toolkit = toolkit;
        _lizzy = lizzy;
        _extractionSchema = SchemaLoader.TryLoad();

        _messageChannel = Channel.CreateUnbounded<IncomingMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _taskCompletionChannel = Channel.CreateUnbounded<TaskCompletion>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async ValueTask NotifyMessage(IncomingMessage message)
    {
        _lastUserContact = DateTime.UtcNow;
        await _messageChannel.Writer.WriteAsync(message);
        _logger.LogInformation("Message received from {UserId}: {Preview}...",
            message.UserId,
            message.Content.Length > 50 ? message.Content[..50] : message.Content);
    }

    public async ValueTask NotifyTaskCompletion(TaskCompletion completion)
    {
        await _taskCompletionChannel.Writer.WriteAsync(completion);
        _logger.LogDebug("Task {TaskId} completed: {Success}", completion.TaskId, completion.Success);
    }

    public async Task<Perception> Perceive()
    {
        var now = DateTime.UtcNow;
        var newlyReceived = await DrainMessages();

        if (newlyReceived.Count > 0)
        {
            _messageBacklog.AddRange(newlyReceived);
        }

        _messageBacklog.RemoveAll(m => m.IsProcessed);

        const int maxBacklog = 200;
        if (_messageBacklog.Count > maxBacklog)
        {
            _messageBacklog.RemoveRange(0, _messageBacklog.Count - maxBacklog);
        }

        int activeGoalsCount = 0;
        int goalsWithPending = 0;
        IReadOnlyList<GoalEvent> goalEvents = Array.Empty<GoalEvent>();
        int pendingMemories = 0;

        try
        {
            activeGoalsCount = await _goals.GetActiveCount();

            // v4: query how many active goals have at least one pending step so
            // the state encoder can populate dimension [13].
            goalsWithPending = await _goals.GetGoalsWithPendingStepsCount();
            goalEvents       = await _goals.GetRecentEvents();
            pendingMemories  = await _memory.GetPendingConsolidationCount();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Perception subsystem query failed — using safe defaults. " +
                "Living loop will continue.");
        }

        var perception = new Perception
        {
            Timestamp                = now,
            TimeSinceLastUserContact = now - _lastUserContact,
            TimeSinceLastAction      = now - _lastAction,
            IsQuietHours             = IsQuietHours(now),
            NewMessages              = _messageBacklog.Where(m => !m.IsProcessed).ToList(),
            CompletedTasks           = await DrainCompletions(),
            GoalEvents               = goalEvents,
            PendingMemoriesToProcess = pendingMemories,
            ActiveGoalsCount         = activeGoalsCount,
            GoalsWithPendingSteps    = goalsWithPending
        };

        if (perception.HasAnythingToNotice)
        {
            _logger.LogDebug(
                "Perception: {MsgCount} messages, {TaskCount} completions, {GoalCount} goal events",
                perception.NewMessages.Count,
                perception.CompletedTasks.Count,
                perception.GoalEvents.Count);
        }

        return perception;
    }

    public void RecordAction()
    {
        _lastAction = DateTime.UtcNow;
    }

    public bool HasUrgentMessages()
    {
        return _messageChannel.Reader.TryPeek(out var msg) && msg.Urgency >= Urgency.Now;
    }

    public async Task<bool> WaitForEventOrTimeout(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var messageTask    = _messageChannel.Reader.WaitToReadAsync(cts.Token).AsTask();
            var completionTask = _taskCompletionChannel.Reader.WaitToReadAsync(cts.Token).AsTask();

            await Task.WhenAny(messageTask, completionTask);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public IReadOnlyList<ProcessedMessage> DrainProcessedMessages()
    {
        var result = _processedBacklog.ToList();
        _processedBacklog.Clear();
        return result;
    }

    private async Task<List<IncomingMessage>> DrainMessages()
    {
        var messages = new List<IncomingMessage>();
        while (_messageChannel.Reader.TryRead(out var msg))
        {
            msg.Intent = ClassifyIntent(msg.Content);

            ComprehensionResult? comprehension = null;
            ExtractionResult? extraction = null;

            if (_lizzy.IsReachable)
            {
                comprehension = await _lizzy.ComprehendAsync(msg.Content);

                if (_extractionSchema is not null)
                    extraction = await _lizzy.ExtractAsync(msg.Content, _extractionSchema);

                // Replace LLM fallback with Lizzy for Unknown intent
                if (msg.Intent.Type == DarciIntentType.Unknown
                    && comprehension.PrimaryIntent != LizzyIntentType.Unknown)
                {
                    msg.Intent = MapLizzyIntent(comprehension);
                    _logger.LogDebug("Lizzy classified intent: {Intent} ({Conf:P0})",
                        msg.Intent.Type, msg.Intent.Confidence);
                }
            }

            // LLM fallback — only if still Unknown after Lizzy (or Lizzy unreachable)
            if (msg.Intent.Type == DarciIntentType.Unknown)
            {
                _logger.LogDebug("LLM classification fallback: {Preview}...",
                    msg.Content.Length > 30 ? msg.Content[..30] : msg.Content);
                msg.Intent = await _toolkit.ClassifyIntent(msg.Content);
            }

            _processedBacklog.Add(new ProcessedMessage
            {
                Source = msg,
                Comprehension = comprehension,
                Extraction = extraction
            });

            messages.Add(msg);
        }
        return messages;
    }

    private static MessageIntent MapLizzyIntent(ComprehensionResult c)
    {
        var darciIntent = c.PrimaryIntent switch
        {
            LizzyIntentType.Conversation          => DarciIntentType.Conversation,
            LizzyIntentType.Question              => DarciIntentType.Question,
            LizzyIntentType.Task                  => DarciIntentType.Task,
            LizzyIntentType.GoalUpdate            => DarciIntentType.GoalUpdate,
            LizzyIntentType.Research              => DarciIntentType.Research,
            LizzyIntentType.CAD                   => DarciIntentType.CAD,
            LizzyIntentType.EngineeringCollection => DarciIntentType.EngineeringCollection,
            LizzyIntentType.StatusCheck           => DarciIntentType.StatusCheck,
            LizzyIntentType.Feedback              => DarciIntentType.Feedback,
            LizzyIntentType.DecisionReference     => DarciIntentType.Question,
            _                                     => DarciIntentType.Unknown,
        };

        return new MessageIntent
        {
            Type           = darciIntent,
            ExtractedTopic = c.ExtractedTopic,
            Confidence     = c.IntentDistribution.Length > (int)c.PrimaryIntent
                                ? c.IntentDistribution[(int)c.PrimaryIntent]
                                : 0.7f,
            Parameters     = c.Entities.Count > 0 ? c.Entities : new()
        };
    }

    private Task<List<TaskCompletion>> DrainCompletions()
    {
        var completions = new List<TaskCompletion>();
        while (_taskCompletionChannel.Reader.TryRead(out var completion))
        {
            completions.Add(completion);
        }
        return Task.FromResult(completions);
    }

    private MessageIntent ClassifyIntent(string content)
    {
        var lower = content.ToLowerInvariant().Trim();

        if (IsClearlyCADRequest(lower))
        {
            return new MessageIntent
            {
                Type = DarciIntentType.CAD,
                ExtractedTopic = content,
                Confidence = 0.85f
            };
        }

        if (HasEngineeringCollectionTag(lower) || IsLikelyEngineeringCollectionRequest(lower))
        {
            return new MessageIntent
            {
                Type = DarciIntentType.EngineeringCollection,
                ExtractedTopic = ExtractCollectionTopic(content),
                Confidence = HasEngineeringCollectionTag(lower) ? 0.98f : 0.75f
            };
        }

        if (IsClearlyConversation(lower))
        {
            return new MessageIntent
            {
                Type = DarciIntentType.Conversation,
                Confidence = 0.9f
            };
        }

        if (ContainsAny(lower, "remind me", "don't let me forget", "dont let me forget"))
        {
            return new MessageIntent
            {
                Type = DarciIntentType.Reminder,
                ExtractedTopic = ExtractTopicAfter(lower, "remind me", "don't let me forget"),
                Confidence = 0.9f
            };
        }

        // Intercept complex multi-domain requests before LLM fallback.
        // Deterministic pattern match — runs in <1ms.
        if (ComplexRequestDetector.IsComplex(content))
        {
            _logger.LogDebug("ComplexRequestDetector: routing to EngineeringCollection");
            return new MessageIntent
            {
                Type = DarciIntentType.EngineeringCollection,
                ExtractedTopic = ExtractCollectionTopic(content),
                Confidence = 0.9f
            };
        }

        if (MightBeActionable(lower))
        {
            return new MessageIntent
            {
                Type = DarciIntentType.Unknown,
                Confidence = 0.0f
            };
        }

        return new MessageIntent
        {
            Type = DarciIntentType.Conversation,
            Confidence = 0.6f
        };
    }

    private bool IsClearlyCADRequest(string text)
    {
        var cadNouns = new[] { "stl", "cad", "3d model", "3d print", "3d part" };
        var cadVerbs = new[] { "generate", "create", "make", "design", "build", "model" };

        if (ContainsAny(text, cadNouns) && ContainsAny(text, cadVerbs))
            return true;

        if (ContainsAny(text,
            "generate a part", "generate a model",
            "mill a ", "cnc ", "lathe ",
            "create an stl", "make an stl",
            "design a bracket", "design a mount", "design a plate",
            "3d model of"))
            return true;

        return false;
    }

    private bool IsClearlyConversation(string text)
    {
        if (StartsWithAny(text, "hi", "hey", "hello", "good morning", "good evening", "yo", "sup"))
            return true;

        if (ContainsAny(text, "how are you", "how's it going", "what's up", "how have you been"))
            return true;

        if (StartsWithAny(text, "i think", "i feel", "i believe", "i was", "i've been", "i had"))
            return true;

        if (StartsWithAny(text, "that's", "thats", "wow", "cool", "nice", "interesting", "i see", "makes sense"))
            return true;

        if (text.Length < 15 && !text.Contains("?"))
            return true;

        return false;
    }

    private bool HasEngineeringCollectionTag(string text)
    {
        return StartsWithAny(text, "#collection", "/collection", "#assembly", "/assembly")
            || StartsWithAny(text, "#collection-file", "/collection-file", "#assembly-file", "/assembly-file")
            || ContainsAny(text, " #collection", " #assembly", " #collection-file", " #assembly-file");
    }

    private bool IsLikelyEngineeringCollectionRequest(string text)
    {
        if (text.StartsWith("{", StringComparison.Ordinal)
            && (text.Contains("\"parts\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\"connections\"", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var assemblyTerms = new[]
        {
            "assembly", "multi-part", "multi part", "system",
            "fit between", "fit check", "clearance",
            "simulate movement", "motion simulation", "kinematic",
            "portal gear project", "gear train"
        };

        return ContainsAny(text, assemblyTerms)
            && ContainsAny(text, "build", "design", "create", "generate", "engineer", "make");
    }

    private bool MightBeActionable(string text)
    {
        var actionWords = new[]
        {
            "research", "look into", "find out", "search", "look up",
            "can you", "could you", "would you", "please",
            "create", "make", "write", "send", "schedule",
            "generate", "design", "build"
        };
        return ContainsAny(text, actionWords);
    }

    private bool StartsWithAny(string text, params string[] patterns)
        => patterns.Any(p => text.StartsWith(p));

    private bool ContainsAny(string text, params string[] patterns)
        => patterns.Any(p => text.Contains(p));

    private string? ExtractTopicAfter(string text, params string[] triggers)
    {
        foreach (var trigger in triggers)
        {
            var idx = text.IndexOf(trigger);
            if (idx >= 0)
            {
                var after = text[(idx + trigger.Length)..].Trim().TrimStart(':', '-', ' ');
                if (after.Length > 0)
                    return after.Length > 200 ? after[..200] : after;
            }
        }
        return null;
    }

    private string ExtractCollectionTopic(string content)
    {
        var trimmed = content.Trim();
        var lower = trimmed.ToLowerInvariant();
        var fileTags = new[] { "#collection-file", "/collection-file", "#assembly-file", "/assembly-file" };
        foreach (var tag in fileTags)
        {
            if (lower.StartsWith(tag, StringComparison.Ordinal))
                return trimmed;
        }

        var tags = new[] { "#collection", "/collection", "#assembly", "/assembly" };
        foreach (var tag in tags)
        {
            if (lower.StartsWith(tag, StringComparison.Ordinal))
            {
                var after = trimmed[tag.Length..].TrimStart(':', '-', ' ', '\n', '\r', '\t');
                return string.IsNullOrWhiteSpace(after) ? trimmed : after;
            }
        }

        return trimmed;
    }

    private bool IsQuietHours(DateTime time)
    {
        var localTime = TimeOnly.FromDateTime(time.ToLocalTime());

        if (_quietStart > _quietEnd)
            return localTime >= _quietStart || localTime < _quietEnd;

        return localTime >= _quietStart && localTime < _quietEnd;
    }
}
