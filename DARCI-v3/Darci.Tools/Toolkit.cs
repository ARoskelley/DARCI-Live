using System.Threading.Channels;
using Darci.Shared;
using Darci.Memory;
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
    private readonly Channel<OutgoingMessage> _outgoingMessages;

    public Toolkit(
        ILogger<Toolkit> logger,
        IOllamaClient ollama,
        IMemoryStore memory)
    {
        _logger = logger;
        _ollama = ollama;
        _memory = memory;

        // Channel for outgoing messages - the API will read from this
        _outgoingMessages = Channel.CreateUnbounded<OutgoingMessage>();
    }

    /// <summary>
    /// Get the reader for outgoing messages (used by API to deliver responses)
    /// </summary>
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
        "goalupdate" => IntentType.GoalUpdate,
        "statuscheck" => IntentType.StatusCheck,
        "feedback" => IntentType.Feedback,
        _ => IntentType.Conversation // Default to conversation if unclear
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
        // For now, use LLM knowledge as a fallback
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

    // === Private Helpers ===

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
}
