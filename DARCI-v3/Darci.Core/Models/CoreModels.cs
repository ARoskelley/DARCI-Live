namespace Darci.Core.Models;

/// <summary>
/// What DARCI can choose to do. These are her verbs.
/// </summary>
public enum ActionType
{
    // Communication
    Reply,              // Respond to a message from the user
    Notify,             // Proactively reach out to the user
    
    // Thinking
    Think,              // Reflect, consider, process internally
    Decide,             // Make a choice about something
    
    // Memory
    Remember,           // Store something in memory
    Recall,             // Retrieve something from memory
    Consolidate,        // Organize and link memories
    
    // Goals
    WorkOnGoal,         // Make progress on an active goal
    CreateGoal,         // Define a new goal
    ReviewGoals,        // Check in on goal progress
    
    // Research
    Research,           // Search the web for information
    ReadFile,           // Examine a document
    WriteFile,          // Create or modify a file
    
    // Meta
    Rest,               // Consciously choose to wait
    Observe,            // Just notice what's happening without acting
}

/// <summary>
/// The urgency of an action or message
/// </summary>
public enum Urgency
{
    Whenever,           // Handle it when you get to it
    Soon,               // Should be addressed relatively quickly  
    Now,                // Needs immediate attention
    Interrupt           // Drop everything
}

/// <summary>
/// What DARCI notices happening in her world
/// </summary>
public class Perception
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    
    // Messages from the user
    public List<IncomingMessage> NewMessages { get; init; } = new();
    public bool HasUrgentMessage => NewMessages.Any(m => m.Urgency >= Urgency.Now);
    
    // Goal-related
    public List<GoalEvent> GoalEvents { get; init; } = new();
    
    // Research/task completions
    public List<TaskCompletion> CompletedTasks { get; init; } = new();
    
    // Time awareness
    public TimeSpan TimeSinceLastUserContact { get; init; }
    public TimeSpan TimeSinceLastAction { get; init; }
    public bool IsQuietHours { get; init; }
    
    // Internal state
    public int PendingMemoriesToProcess { get; init; }
    public int ActiveGoalsCount { get; init; }
    
    public bool HasAnythingToNotice => 
        NewMessages.Any() || 
        GoalEvents.Any() || 
        CompletedTasks.Any() ||
        PendingMemoriesToProcess > 0;
}

/// <summary>
/// A message from the user
/// </summary>
public class IncomingMessage
{
    public int Id { get; init; }
    public string Content { get; init; } = "";
    public string UserId { get; init; } = "Tinman";
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    public Urgency Urgency { get; init; } = Urgency.Soon;
    public bool IsProcessed { get; set; } = false;
    
    // Parsed intent (filled in during processing)
    public MessageIntent? Intent { get; set; }
}

/// <summary>
/// What the user seems to want (classified without LLM when possible)
/// </summary>
public class MessageIntent
{
    public IntentType Type { get; init; }
    public string? ExtractedTopic { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new();
    public float Confidence { get; init; } = 1.0f;
}

public enum IntentType
{
    Conversation,       // Just chatting
    Question,           // Asking something
    Research,           // "Look into X", "Find out about Y"
    Task,               // "Do X", "Create Y"
    Reminder,           // "Remind me", "Don't let me forget"
    GoalUpdate,         // "I finished X", "Cancel Y"
    StatusCheck,        // "How's X going?", "What are you working on?"
    Feedback,           // "Good job", "That's wrong"
    Unknown             // Needs LLM to classify
}

/// <summary>
/// Something that happened with a goal
/// </summary>
public class GoalEvent
{
    public int GoalId { get; init; }
    public GoalEventType Type { get; init; }
    public string? Details { get; init; }
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

public enum GoalEventType
{
    Created,
    ProgressMade,
    Blocked,
    Completed,
    Abandoned,
    DueSoon,
    Overdue
}

/// <summary>
/// A background task that finished
/// </summary>
public class TaskCompletion
{
    public int TaskId { get; init; }
    public string TaskType { get; init; } = "";
    public bool Success { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// What DARCI decides to do
/// </summary>
public class DarciAction
{
    public ActionType Type { get; init; }
    public Urgency Urgency { get; init; } = Urgency.Soon;
    
    // For communication
    public string? RecipientId { get; init; }
    public string? MessageContent { get; init; }
    
    // For thinking/generation
    public string? Prompt { get; init; }
    public string? Topic { get; init; }
    
    // For memory
    public string? MemoryContent { get; init; }
    public List<string>? Tags { get; init; }
    
    // For goals
    public int? GoalId { get; init; }
    public string? GoalDescription { get; init; }
    
    // For research
    public string? Query { get; init; }
    public string? FilePath { get; init; }
    public string? FileContent { get; init; }
    
    // For rest
    public TimeSpan RestDuration { get; init; } = TimeSpan.FromMilliseconds(100);
    
    // Why DARCI chose this
    public string? Reasoning { get; init; }
    
    // Reference to what triggered this (if any)
    public int? InResponseToMessageId { get; init; }
    public int? InResponseToGoalId { get; init; }
    
    public static DarciAction Rest(TimeSpan? duration = null, string? reason = null) => new()
    {
        Type = ActionType.Rest,
        RestDuration = duration ?? TimeSpan.FromMilliseconds(500),
        Reasoning = reason ?? "Nothing needs my attention right now"
    };
    
    public static DarciAction Reply(string content, string userId, int? messageId = null, string? reason = null) => new()
    {
        Type = ActionType.Reply,
        MessageContent = content,
        RecipientId = userId,
        InResponseToMessageId = messageId,
        Reasoning = reason
    };
    
    public static DarciAction Think(string topic, string? reason = null) => new()
    {
        Type = ActionType.Think,
        Topic = topic,
        Reasoning = reason
    };
    
    public static DarciAction Research(string query, int? forGoalId = null, string? reason = null) => new()
    {
        Type = ActionType.Research,
        Query = query,
        InResponseToGoalId = forGoalId,
        Reasoning = reason
    };
    
    public static DarciAction WorkOn(int goalId, string? reason = null) => new()
    {
        Type = ActionType.WorkOnGoal,
        GoalId = goalId,
        Reasoning = reason
    };
}

/// <summary>
/// What happened as a result of an action
/// </summary>
public class Outcome
{
    public bool Success { get; init; }
    public ActionType ActionTaken { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
    
    // For tracking
    public int? MessageIdHandled { get; init; }
    public int? GoalIdProgressed { get; init; }
    
    public static Outcome Succeeded(ActionType action, object? result = null) => new()
    {
        Success = true,
        ActionTaken = action,
        Result = result
    };
    
    public static Outcome Failed(ActionType action, string error) => new()
    {
        Success = false,
        ActionTaken = action,
        Error = error
    };
    
    public static Outcome Rested() => new()
    {
        Success = true,
        ActionTaken = ActionType.Rest
    };
}
