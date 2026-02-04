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
}
