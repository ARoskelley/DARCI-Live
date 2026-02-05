using System.Threading.Channels;
using Darci.Shared;
using Darci.Memory;
using Darci.Goals;
using Microsoft.Extensions.Logging;
using Darci.Tools;

namespace Darci.Core;

/// <summary>
/// DARCI's awareness system - how she perceives her world.
/// This aggregates all the things she might notice: messages, goal events, completions, etc.
/// </summary>


public class Awareness
{
    private readonly ILogger<Awareness> _logger;
    private readonly IMemoryStore _memory;
    private readonly IGoalManager _goals;
    private readonly Channel<IncomingMessage> _messageChannel;
    private readonly Channel<TaskCompletion> _taskCompletionChannel;
    private readonly IToolkit _toolkit;
    private DateTime _lastUserContact = DateTime.UtcNow;
    private DateTime _lastAction = DateTime.UtcNow;

    // Quiet hours configuration (can be made configurable)
    private readonly TimeOnly _quietStart = new(0, 0);   // Midnight
    private readonly TimeOnly _quietEnd = new(6, 0);     // 6 AM

    public Awareness(
        ILogger<Awareness> logger,
        IMemoryStore memory,
        IGoalManager goals,
        IToolkit toolkit)
    {
        _logger = logger;
        _memory = memory;
        _goals = goals;
        _toolkit = toolkit;

        // Unbounded channels - DARCI will process at her own pace
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

    /// <summary>
    /// Called by the API when a message arrives from the user.
    /// This doesn't block - it just adds to DARCI's awareness.
    /// </summary>
    public async ValueTask NotifyMessage(IncomingMessage message)
    {
        _lastUserContact = DateTime.UtcNow;
        await _messageChannel.Writer.WriteAsync(message);
        _logger.LogInformation("Message received from {UserId}: {Preview}...",
            message.UserId,
            message.Content.Length > 50 ? message.Content[..50] : message.Content);
    }

    /// <summary>
    /// Called when a background task completes (research, file operation, etc.)
    /// </summary>
    public async ValueTask NotifyTaskCompletion(TaskCompletion completion)
    {
        await _taskCompletionChannel.Writer.WriteAsync(completion);
        _logger.LogDebug("Task {TaskId} completed: {Success}", completion.TaskId, completion.Success);
    }

    /// <summary>
    /// DARCI calls this to perceive her current state.
    /// Gathers everything she might want to notice.
    /// </summary>
    public async Task<Perception> Perceive()
    {
        var now = DateTime.UtcNow;
        var perception = new Perception
        {
            Timestamp = now,
            TimeSinceLastUserContact = now - _lastUserContact,
            TimeSinceLastAction = now - _lastAction,
            IsQuietHours = IsQuietHours(now),
            NewMessages = await DrainMessages(),
            CompletedTasks = await DrainCompletions(),
            GoalEvents = await _goals.GetRecentEvents(),
            PendingMemoriesToProcess = await _memory.GetPendingConsolidationCount(),
            ActiveGoalsCount = await _goals.GetActiveCount()
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

    /// <summary>
    /// Marks that DARCI took an action (for tracking idle time)
    /// </summary>
    public void RecordAction()
    {
        _lastAction = DateTime.UtcNow;
    }

    /// <summary>
    /// Check if there are any urgent messages waiting
    /// </summary>
    public bool HasUrgentMessages()
    {
        // Peek without consuming
        return _messageChannel.Reader.TryPeek(out var msg) && msg.Urgency >= Urgency.Now;
    }

    /// <summary>
    /// Wait for something to happen (message, completion, or timeout)
    /// This is how DARCI can "rest" efficiently without burning CPU
    /// </summary>
    public async Task<bool> WaitForEventOrTimeout(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            // Wait for either channel to have something
            var messageTask = _messageChannel.Reader.WaitToReadAsync(cts.Token).AsTask();
            var completionTask = _taskCompletionChannel.Reader.WaitToReadAsync(cts.Token).AsTask();

            await Task.WhenAny(messageTask, completionTask);
            return true; // Something arrived
        }
        catch (OperationCanceledException)
        {
            return false; // Timeout - nothing happened
        }
    }

    private async Task<List<IncomingMessage>> DrainMessages()
    {
        var messages = new List<IncomingMessage>();
        while (_messageChannel.Reader.TryRead(out var msg))
        {
            // Quick classify first
            msg.Intent = ClassifyIntent(msg.Content);

            // If unknown, ask LLM to classify
            if (msg.Intent.Type == IntentType.Unknown)
            {
                _logger.LogDebug("Message needs LLM classification: {Preview}...",
                    msg.Content.Length > 30 ? msg.Content[..30] : msg.Content);
                msg.Intent = await _toolkit.ClassifyIntent(msg.Content);
            }

            messages.Add(msg);
        }
        return messages;
    }

    private async Task<List<TaskCompletion>> DrainCompletions()
    {
        var completions = new List<TaskCompletion>();
        while (_taskCompletionChannel.Reader.TryRead(out var completion))
        {
            completions.Add(completion);
        }
        return completions;
    }

    /// <summary>
    /// Quick intent classification without LLM.
    /// Returns Unknown if it needs deeper analysis.
    /// </summary>
    /// <summary>
    /// Quick intent classification - uses LLM only when needed.
    /// </summary>
    private MessageIntent ClassifyIntent(string content)
    {
        var lower = content.ToLowerInvariant().Trim();

        // Definitely conversation - no need for LLM
        if (IsClearlyConversation(lower))
        {
            return new MessageIntent
            {
                Type = IntentType.Conversation,
                Confidence = 0.9f
            };
        }

        // Definitely a reminder request
        if (ContainsAny(lower, "remind me", "don't let me forget", "dont let me forget"))
        {
            return new MessageIntent
            {
                Type = IntentType.Reminder,
                ExtractedTopic = ExtractTopicAfter(lower, "remind me", "don't let me forget"),
                Confidence = 0.9f
            };
        }

        // Might be actionable - flag for LLM classification
        if (MightBeActionable(lower))
        {
            return new MessageIntent
            {
                Type = IntentType.Unknown,  // Will trigger LLM classification
                Confidence = 0.0f
            };
        }

        // Default to conversation
        return new MessageIntent
        {
            Type = IntentType.Conversation,
            Confidence = 0.6f
        };
    }

    /// <summary>
    /// Messages that are clearly just conversation - no action needed
    /// </summary>
    private bool IsClearlyConversation(string text)
    {
        // Greetings
        if (StartsWithAny(text, "hi", "hey", "hello", "good morning", "good evening", "yo", "sup"))
            return true;

        // How are you type messages
        if (ContainsAny(text, "how are you", "how's it going", "what's up", "how have you been"))
            return true;

        // Sharing/venting (not asking for action)
        if (StartsWithAny(text, "i think", "i feel", "i believe", "i was", "i've been", "i had"))
            return true;

        // Opinions and reactions
        if (StartsWithAny(text, "that's", "thats", "wow", "cool", "nice", "interesting", "i see", "makes sense"))
            return true;

        // Very short messages are usually conversational
        if (text.Length < 15 && !text.Contains("?"))
            return true;

        return false;
    }

    /// <summary>
    /// Check if message might be requesting an action (worth asking LLM)
    /// </summary>
    private bool MightBeActionable(string text)
    {
        // Action words that might indicate a request
        var actionWords = new[] { "research", "look into", "find out", "search", "look up",
                               "can you", "could you", "would you", "please",
                               "create", "make", "write", "send", "schedule" };

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
                var after = text[(idx + trigger.Length)..].Trim();
                // Clean up common suffixes
                after = after.TrimStart(':', '-', ' ');
                if (after.Length > 0)
                    return after.Length > 200 ? after[..200] : after;
            }
        }
        return null;
    }

    private bool IsQuietHours(DateTime time)
    {
        var localTime = TimeOnly.FromDateTime(time.ToLocalTime());

        // Handle overnight quiet hours (e.g., 11 PM to 6 AM)
        if (_quietStart > _quietEnd)
        {
            return localTime >= _quietStart || localTime < _quietEnd;
        }

        return localTime >= _quietStart && localTime < _quietEnd;
    }
}
