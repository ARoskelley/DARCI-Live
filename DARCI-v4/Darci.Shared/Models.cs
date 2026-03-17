namespace Darci.Shared;

// ============================================================
// ACTION TYPES - What DARCI can choose to do
// ============================================================

public enum ActionType
{
    // Communication
    Reply,
    Notify,

    // Thinking
    Think,
    Decide,

    // Memory
    Remember,
    Recall,
    Consolidate,

    // Goals
    WorkOnGoal,
    CreateGoal,
    ReviewGoals,

    // Research
    Research,
    ReadFile,
    WriteFile,

    // CAD
    GenerateCAD,
    Engineer,

    // Neural Engineering (geometry workbench + ONNX network)
    Engineering,

    // Meta
    Rest,
    Observe,
}

public enum Urgency
{
    Whenever,
    Soon,
    Now,
    Interrupt
}

// ============================================================
// MOOD - DARCI's emotional states
// ============================================================

public enum Mood
{
    Calm,
    Alert,
    Curious,
    Content,
    Satisfied,
    Frustrated,
    Reflective,
    Playful
}

// ============================================================
// MESSAGE TYPES
// ============================================================

public class IncomingMessage
{
    public int Id { get; init; }
    public string Content { get; init; } = "";
    public string UserId { get; init; } = "Tinman";
    public string Source { get; init; } = "api";
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
    public Urgency Urgency { get; init; } = Urgency.Soon;
    public bool IsProcessed { get; set; } = false;
    public MessageIntent? Intent { get; set; }
}

public class MessageIntent
{
    public IntentType Type { get; init; }
    public string? ExtractedTopic { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new();
    public float Confidence { get; init; } = 1.0f;
}

public enum IntentType
{
    Conversation,
    Question,
    Research,
    Task,
    Reminder,
    GoalUpdate,
    StatusCheck,
    Feedback,
    CAD,
    EngineeringCollection,
    Unknown
}

public class OutgoingMessage
{
    public string UserId { get; init; } = "";
    public string Content { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public int? InResponseToMessageId { get; init; }
    public bool ExternalNotify { get; init; } = false;
}

// ============================================================
// PERCEPTION - What DARCI notices
// ============================================================

public class Perception
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public List<IncomingMessage> NewMessages { get; init; } = new();
    public bool HasUrgentMessage => NewMessages.Any(m => m.Urgency >= Urgency.Now);
    public List<GoalEvent> GoalEvents { get; init; } = new();
    public List<TaskCompletion> CompletedTasks { get; init; } = new();
    public TimeSpan TimeSinceLastUserContact { get; init; }
    public TimeSpan TimeSinceLastAction { get; init; }
    public bool IsQuietHours { get; init; }
    public int PendingMemoriesToProcess { get; init; }
    public int ActiveGoalsCount { get; init; }
    public int GoalsWithPendingSteps { get; init; }

    public bool HasAnythingToNotice =>
        NewMessages.Any() ||
        GoalEvents.Any() ||
        CompletedTasks.Any() ||
        PendingMemoriesToProcess > 0;
}

// ============================================================
// GOAL TYPES
// ============================================================

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

public class TaskCompletion
{
    public int TaskId { get; init; }
    public string TaskType { get; init; } = "";
    public bool Success { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
}

public class Goal
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string UserId { get; set; } = "";
    public GoalType Type { get; set; } = GoalType.Task;
    public GoalPriority Priority { get; set; } = GoalPriority.Medium;
    public GoalStatus Status { get; set; } = GoalStatus.Active;
    public GoalSource Source { get; set; } = GoalSource.UserRequested;
    public DateTime CreatedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<GoalStep> Steps { get; set; } = new();
    public string? Notes { get; set; }
}

public class GoalCreation
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string UserId { get; set; } = "Tinman";
    public GoalType Type { get; set; } = GoalType.Task;
    public GoalPriority Priority { get; set; } = GoalPriority.Medium;
    public GoalSource Source { get; set; } = GoalSource.UserRequested;
    public DateTime? DueAt { get; set; }
}

public class GoalStep
{
    public int Id { get; set; }
    public int GoalId { get; set; }
    public int Order { get; set; }
    public GoalStepType Type { get; set; }
    public string Description { get; set; } = "";
    public GoalStepStatus Status { get; set; } = GoalStepStatus.Pending;
    public string? Query { get; set; }
    public string? Prompt { get; set; }
    public string? Message { get; set; }
}

public enum GoalType
{
    Task,
    Research,
    Reminder,
    Project,
    Learning,
    CAD
}

public enum GoalPriority
{
    Low,
    Medium,
    High,
    Urgent
}

public enum GoalStatus
{
    Active,
    InProgress,
    Blocked,
    Completed,
    Abandoned
}

public enum GoalSource
{
    UserRequested,
    DarciInitiated,
    System
}

public enum GoalStepType
{
    Research,
    Generate,
    Engineer,
    Notify,
    Wait,
    Custom,
    CAD
}

public enum GoalStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped
}

// ============================================================
// ACTION & OUTCOME
// ============================================================

public class DarciAction
{
    public ActionType Type { get; init; }
    public Urgency Urgency { get; init; } = Urgency.Soon;

    public string? RecipientId { get; init; }
    public string? MessageContent { get; init; }
    public string? Prompt { get; init; }
    public string? Topic { get; init; }
    public string? MemoryContent { get; init; }
    public List<string>? Tags { get; init; }
    public int? GoalId { get; init; }
    public string? GoalDescription { get; init; }
    public string? Query { get; init; }
    public string? FilePath { get; init; }
    public string? FileContent { get; init; }
    public TimeSpan RestDuration { get; init; } = TimeSpan.FromMilliseconds(100);
    public string? Reasoning { get; init; }
    public bool ExternalNotify { get; init; } = false;
    public int? InResponseToMessageId { get; init; }
    public int? InResponseToGoalId { get; init; }
    public bool ProgressGoalStepOnSuccess { get; init; } = false;

    // CAD-specific fields
    public string? CadDescription { get; init; }
    public float? CadLengthMm { get; init; }
    public float? CadWidthMm { get; init; }
    public float? CadHeightMm { get; init; }
    public int CadMaxIterations { get; init; } = 5;

    // Engineering-specific fields (v3 LLM path)
    public string? EngineeringDescription { get; init; }
    public int EngineeringMaxIterations { get; init; } = 3;
    public bool EngineeringRunCollection { get; init; } = false;

    // Neural Engineering fields (v4 workbench + ONNX path)
    public EngineeringGoalSpec? EngineeringSpec { get; init; }

    public static DarciAction Rest(TimeSpan? duration = null, string? reason = null) => new()
    {
        Type = ActionType.Rest,
        RestDuration = duration ?? TimeSpan.FromMilliseconds(500),
        Reasoning = reason ?? "Nothing needs my attention right now"
    };

    public static DarciAction Reply(
        string content,
        string userId,
        int? messageId = null,
        string? reason = null,
        bool externalNotify = false) => new()
    {
        Type = ActionType.Reply,
        MessageContent = content,
        RecipientId = userId,
        ExternalNotify = externalNotify,
        InResponseToMessageId = messageId,
        Reasoning = reason
    };

    public static DarciAction Think(string topic, string? reason = null) => new()
    {
        Type = ActionType.Think,
        Topic = topic,
        Reasoning = reason
    };

    public static DarciAction Research(
        string query,
        int? forGoalId = null,
        string? reason = null,
        bool progressGoalStepOnSuccess = false) => new()
    {
        Type = ActionType.Research,
        Query = query,
        InResponseToGoalId = forGoalId,
        ProgressGoalStepOnSuccess = progressGoalStepOnSuccess,
        Reasoning = reason
    };

    public static DarciAction WorkOn(int goalId, string? reason = null) => new()
    {
        Type = ActionType.WorkOnGoal,
        GoalId = goalId,
        Reasoning = reason
    };

    public static DarciAction GenerateCad(
        string description,
        string? userId = null,
        int? messageId = null,
        int? forGoalId = null,
        float? lengthMm = null,
        float? widthMm = null,
        float? heightMm = null,
        int maxIterations = 5,
        bool progressGoalStepOnSuccess = false,
        string? reason = null) => new()
    {
        Type = ActionType.GenerateCAD,
        CadDescription = description,
        CadLengthMm = lengthMm,
        CadWidthMm = widthMm,
        CadHeightMm = heightMm,
        CadMaxIterations = maxIterations,
        RecipientId = userId,
        InResponseToMessageId = messageId,
        InResponseToGoalId = forGoalId,
        ProgressGoalStepOnSuccess = progressGoalStepOnSuccess,
        Reasoning = reason ?? "Generating CAD model"
    };

    public static DarciAction Engineer(
        string description,
        string? userId = null,
        int? messageId = null,
        int? forGoalId = null,
        int maxIterations = 3,
        bool progressGoalStepOnSuccess = false,
        string? reason = null) => new()
    {
        Type = ActionType.Engineer,
        EngineeringDescription = description,
        EngineeringMaxIterations = maxIterations,
        RecipientId = userId,
        InResponseToMessageId = messageId,
        InResponseToGoalId = forGoalId,
        ProgressGoalStepOnSuccess = progressGoalStepOnSuccess,
        Reasoning = reason ?? "Running engineering workbench step"
    };

    public static DarciAction EngineerCollection(
        string description,
        string? userId = null,
        int? messageId = null,
        int? forGoalId = null,
        bool progressGoalStepOnSuccess = false,
        string? reason = null) => new()
    {
        Type = ActionType.Engineer,
        EngineeringDescription = description,
        EngineeringRunCollection = true,
        RecipientId = userId,
        InResponseToMessageId = messageId,
        InResponseToGoalId = forGoalId,
        ProgressGoalStepOnSuccess = progressGoalStepOnSuccess,
        Reasoning = reason ?? "Running engineering collection pipeline"
    };
}

public class Outcome
{
    public bool Success { get; init; }
    public ActionType ActionTaken { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
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

// ============================================================
// REPLY CONTEXT - For generating replies
// ============================================================

public class ReplyContext
{
    public string UserMessage { get; init; } = "";
    public string UserId { get; init; } = "";
    public string DarciState { get; init; } = "";
    public MessageIntent? Intent { get; init; }
    public List<string> RelevantMemories { get; set; } = new();
    public List<string> ActiveGoals { get; set; } = new();
}

// ============================================================
// PERSONALITY TYPES
// ============================================================

public class PersonalityTraits
{
    public float Warmth { get; set; } = 0.6f;
    public float HumorAffinity { get; set; } = 0.3f;
    public float Reflectiveness { get; set; } = 0.5f;
    public float Confidence { get; set; } = 0.7f;
    public float Trust { get; set; } = 0.4f;
    public float Curiosity { get; set; } = 0.6f;
    public float BaselineEnergy { get; set; } = 0.7f;
}

public class PersonalityState
{
    public Mood Mood { get; set; } = Mood.Calm;
    public float MoodIntensity { get; set; } = 0.3f;
    public float Energy { get; set; } = 0.7f;
    public float Focus { get; set; } = 0.5f;
}

public enum TraitType
{
    Warmth,
    HumorAffinity,
    Reflectiveness,
    Confidence,
    Trust,
    Curiosity
}

// ============================================================
// ENGINEERING GOAL SPEC — used by DarciAction.EngineeringSpec
// Lives in Darci.Shared so DarciAction and Decision can reference it
// without a dependency on Darci.Engineering.
// ============================================================

/// <summary>
/// Specification for a neural-engineering task.
/// Extracted from a DARCI goal by EngineeringGoalDetector.
/// </summary>
public record EngineeringGoalSpec
{
    public string Description { get; init; } = "";
    public string? ReferencePath { get; init; }
    public Dictionary<string, object>? Constraints { get; init; }
    public Dictionary<string, float>? Targets { get; init; }
    public string ToolId { get; init; } = "geometry_workbench";
}

// ============================================================
// MEMORY TYPES
// ============================================================

public class MemoryEntry
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public string[] Tags { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
    public float Importance { get; set; } = 0.5f;
    public float RelevanceScore { get; set; }
}
