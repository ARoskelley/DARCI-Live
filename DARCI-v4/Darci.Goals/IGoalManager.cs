using Darci.Shared;

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

    /// <summary>
    /// v4 addition: count of active goals that have at least one pending or in-progress step.
    /// Feeds state vector dimension [13] (goals_with_pending_steps).
    /// </summary>
    Task<int> GetGoalsWithPendingStepsCount();

    /// <summary>
    /// Appends a new pending step to an existing goal.
    /// Used by GoalDecomposer to populate steps after LLM decomposition.
    /// </summary>
    Task AddStepAsync(int goalId, string description);
}
