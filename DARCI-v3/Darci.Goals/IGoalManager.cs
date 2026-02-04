using Darci.Core.Models;

namespace Darci.Goals;

/// <summary>
/// Manages DARCI's goals - things she's working toward
/// </summary>
public interface IGoalManager
{
    Task<Goal> CreateGoal(GoalCreation creation);
    Task<Goal?> GetGoal(int id);
    Task<List<Goal>> GetActiveGoals(string? userId = null);
    Task<Goal?> GetNextActionableGoal();
    Task<GoalStep?> GetNextStep(int goalId);
    Task<List<GoalEvent>> GetRecentEvents(int limit = 10);
    Task<int> GetActiveCount();
    Task UpdateGoalStatus(int goalId, GoalStatus status);
    Task AddProgress(int goalId, string progressNote);
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
    
    // Type-specific fields
    public string? Query { get; set; }      // For Research
    public string? Prompt { get; set; }     // For Generate
    public string? Message { get; set; }    // For Notify
}

public enum GoalType
{
    Task,
    Research,
    Reminder,
    Project,
    Learning
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
    Notify,
    Wait,
    Custom
}

public enum GoalStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped
}
