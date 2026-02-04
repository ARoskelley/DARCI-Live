using System.Text.Json;
using Dapper;
using Darci.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Goals;

public class GoalManager : IGoalManager
{
    private readonly ILogger<GoalManager> _logger;
    private readonly string _connectionString;
    
    public GoalManager(ILogger<GoalManager> logger, string connectionString)
    {
        _logger = logger;
        _connectionString = connectionString;
        InitializeDatabase();
    }
    
    public async Task<Goal> CreateGoal(GoalCreation creation)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        var id = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Goals (Title, Description, UserId, Type, Priority, Status, Source, CreatedAt, DueAt)
            VALUES (@Title, @Description, @UserId, @Type, @Priority, 'Active', @Source, @CreatedAt, @DueAt);
            SELECT last_insert_rowid();",
            new
            {
                creation.Title,
                creation.Description,
                creation.UserId,
                Type = creation.Type.ToString(),
                Priority = creation.Priority.ToString(),
                Source = creation.Source.ToString(),
                CreatedAt = DateTime.UtcNow.ToString("o"),
                DueAt = creation.DueAt?.ToString("o")
            });
        
        _logger.LogInformation("Created goal {Id}: {Title}", id, creation.Title);
        
        // Create initial steps based on goal type
        await CreateInitialSteps(id, creation);
        
        return await GetGoal(id) ?? throw new Exception("Failed to create goal");
    }
    
    public async Task<Goal?> GetGoal(int id)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        var record = await conn.QueryFirstOrDefaultAsync<GoalRecord>(
            "SELECT * FROM Goals WHERE Id = @Id", new { Id = id });
        
        if (record == null) return null;
        
        var steps = await conn.QueryAsync<GoalStepRecord>(
            "SELECT * FROM GoalSteps WHERE GoalId = @GoalId ORDER BY StepOrder",
            new { GoalId = id });
        
        return MapToGoal(record, steps.ToList());
    }
    
    public async Task<List<Goal>> GetActiveGoals(string? userId = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        var sql = userId != null
            ? "SELECT * FROM Goals WHERE Status IN ('Active', 'InProgress') AND UserId = @UserId ORDER BY Priority DESC, CreatedAt"
            : "SELECT * FROM Goals WHERE Status IN ('Active', 'InProgress') ORDER BY Priority DESC, CreatedAt";
        
        var records = await conn.QueryAsync<GoalRecord>(sql, new { UserId = userId });
        
        var goals = new List<Goal>();
        foreach (var record in records)
        {
            var steps = await conn.QueryAsync<GoalStepRecord>(
                "SELECT * FROM GoalSteps WHERE GoalId = @GoalId ORDER BY StepOrder",
                new { GoalId = record.Id });
            goals.Add(MapToGoal(record, steps.ToList()));
        }
        
        return goals;
    }
    
    public async Task<Goal?> GetNextActionableGoal()
    {
        using var conn = new SqliteConnection(_connectionString);
        
        // Get highest priority goal that has pending steps
        var record = await conn.QueryFirstOrDefaultAsync<GoalRecord>(@"
            SELECT g.* FROM Goals g
            INNER JOIN GoalSteps s ON s.GoalId = g.Id
            WHERE g.Status IN ('Active', 'InProgress')
            AND s.Status = 'Pending'
            ORDER BY 
                CASE g.Priority 
                    WHEN 'Urgent' THEN 0 
                    WHEN 'High' THEN 1 
                    WHEN 'Medium' THEN 2 
                    ELSE 3 
                END,
                g.CreatedAt
            LIMIT 1");
        
        if (record == null) return null;
        
        var steps = await conn.QueryAsync<GoalStepRecord>(
            "SELECT * FROM GoalSteps WHERE GoalId = @GoalId ORDER BY StepOrder",
            new { GoalId = record.Id });
        
        return MapToGoal(record, steps.ToList());
    }
    
    public async Task<GoalStep?> GetNextStep(int goalId)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        var record = await conn.QueryFirstOrDefaultAsync<GoalStepRecord>(@"
            SELECT * FROM GoalSteps 
            WHERE GoalId = @GoalId AND Status = 'Pending'
            ORDER BY StepOrder
            LIMIT 1",
            new { GoalId = goalId });
        
        return record == null ? null : MapToStep(record);
    }
    
    public async Task<List<GoalEvent>> GetRecentEvents(int limit = 10)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        var records = await conn.QueryAsync<GoalEventRecord>(@"
            SELECT * FROM GoalEvents 
            ORDER BY OccurredAt DESC 
            LIMIT @Limit",
            new { Limit = limit });
        
        return records.Select(r => new GoalEvent
        {
            GoalId = r.GoalId,
            Type = Enum.Parse<GoalEventType>(r.EventType),
            Details = r.Details,
            OccurredAt = DateTime.Parse(r.OccurredAt)
        }).ToList();
    }
    
    public async Task<int> GetActiveCount()
    {
        using var conn = new SqliteConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Goals WHERE Status IN ('Active', 'InProgress')");
    }
    
    public async Task UpdateGoalStatus(int goalId, GoalStatus status)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        await conn.ExecuteAsync(@"
            UPDATE Goals SET Status = @Status, CompletedAt = @CompletedAt WHERE Id = @Id",
            new
            {
                Id = goalId,
                Status = status.ToString(),
                CompletedAt = status == GoalStatus.Completed ? DateTime.UtcNow.ToString("o") : null
            });
        
        // Record event
        await RecordEvent(conn, goalId, status == GoalStatus.Completed 
            ? GoalEventType.Completed 
            : GoalEventType.ProgressMade);
        
        _logger.LogInformation("Goal {Id} status updated to {Status}", goalId, status);
    }
    
    public async Task AddProgress(int goalId, string progressNote)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        // Mark current step complete
        await conn.ExecuteAsync(@"
            UPDATE GoalSteps SET Status = 'Completed' 
            WHERE GoalId = @GoalId AND Status = 'InProgress'",
            new { GoalId = goalId });
        
        // Update goal status
        await conn.ExecuteAsync(@"
            UPDATE Goals SET Status = 'InProgress', Notes = COALESCE(Notes || '\n', '') || @Note 
            WHERE Id = @Id",
            new { Id = goalId, Note = $"[{DateTime.UtcNow:g}] {progressNote}" });
        
        // Check if all steps complete
        var pendingSteps = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM GoalSteps WHERE GoalId = @GoalId AND Status = 'Pending'",
            new { GoalId = goalId });
        
        if (pendingSteps == 0)
        {
            await UpdateGoalStatus(goalId, GoalStatus.Completed);
        }
        else
        {
            await RecordEvent(conn, goalId, GoalEventType.ProgressMade, progressNote);
        }
    }
    
    private async Task CreateInitialSteps(int goalId, GoalCreation creation)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        var steps = creation.Type switch
        {
            GoalType.Research => new[]
            {
                (GoalStepType.Research, $"Search for information about: {creation.Title}"),
                (GoalStepType.Generate, "Summarize findings"),
                (GoalStepType.Notify, "Share results with user")
            },
            GoalType.Reminder => new[]
            {
                (GoalStepType.Notify, creation.Description)
            },
            _ => new[]
            {
                (GoalStepType.Generate, $"Plan approach for: {creation.Title}"),
                (GoalStepType.Notify, "Update user on completion")
            }
        };
        
        for (int i = 0; i < steps.Length; i++)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO GoalSteps (GoalId, StepOrder, Type, Description, Status, Query, Prompt, Message)
                VALUES (@GoalId, @Order, @Type, @Description, 'Pending', @Query, @Prompt, @Message)",
                new
                {
                    GoalId = goalId,
                    Order = i,
                    Type = steps[i].Item1.ToString(),
                    Description = steps[i].Item2,
                    Query = steps[i].Item1 == GoalStepType.Research ? creation.Title : null,
                    Prompt = steps[i].Item1 == GoalStepType.Generate ? steps[i].Item2 : null,
                    Message = steps[i].Item1 == GoalStepType.Notify ? steps[i].Item2 : null
                });
        }
    }
    
    private async Task RecordEvent(SqliteConnection conn, int goalId, GoalEventType type, string? details = null)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO GoalEvents (GoalId, EventType, Details, OccurredAt)
            VALUES (@GoalId, @EventType, @Details, @OccurredAt)",
            new
            {
                GoalId = goalId,
                EventType = type.ToString(),
                Details = details,
                OccurredAt = DateTime.UtcNow.ToString("o")
            });
    }
    
    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS Goals (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                Description TEXT,
                UserId TEXT NOT NULL,
                Type TEXT NOT NULL,
                Priority TEXT NOT NULL,
                Status TEXT NOT NULL,
                Source TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                DueAt TEXT,
                CompletedAt TEXT,
                Notes TEXT
            );
            
            CREATE TABLE IF NOT EXISTS GoalSteps (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GoalId INTEGER NOT NULL,
                StepOrder INTEGER NOT NULL,
                Type TEXT NOT NULL,
                Description TEXT NOT NULL,
                Status TEXT NOT NULL,
                Query TEXT,
                Prompt TEXT,
                Message TEXT,
                FOREIGN KEY (GoalId) REFERENCES Goals(Id)
            );
            
            CREATE TABLE IF NOT EXISTS GoalEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GoalId INTEGER NOT NULL,
                EventType TEXT NOT NULL,
                Details TEXT,
                OccurredAt TEXT NOT NULL,
                FOREIGN KEY (GoalId) REFERENCES Goals(Id)
            );
            
            CREATE INDEX IF NOT EXISTS idx_goals_status ON Goals(Status);
            CREATE INDEX IF NOT EXISTS idx_goals_user ON Goals(UserId);
            CREATE INDEX IF NOT EXISTS idx_goalsteps_goal ON GoalSteps(GoalId);
        ");
        
        _logger.LogInformation("Goals database initialized");
    }
    
    private Goal MapToGoal(GoalRecord r, List<GoalStepRecord> steps) => new()
    {
        Id = r.Id,
        Title = r.Title,
        Description = r.Description ?? "",
        UserId = r.UserId,
        Type = Enum.Parse<GoalType>(r.Type),
        Priority = Enum.Parse<GoalPriority>(r.Priority),
        Status = Enum.Parse<GoalStatus>(r.Status),
        Source = Enum.Parse<GoalSource>(r.Source),
        CreatedAt = DateTime.Parse(r.CreatedAt),
        DueAt = r.DueAt != null ? DateTime.Parse(r.DueAt) : null,
        CompletedAt = r.CompletedAt != null ? DateTime.Parse(r.CompletedAt) : null,
        Steps = steps.Select(MapToStep).ToList(),
        Notes = r.Notes
    };
    
    private GoalStep MapToStep(GoalStepRecord r) => new()
    {
        Id = r.Id,
        GoalId = r.GoalId,
        Order = r.StepOrder,
        Type = Enum.Parse<GoalStepType>(r.Type),
        Description = r.Description,
        Status = Enum.Parse<GoalStepStatus>(r.Status),
        Query = r.Query,
        Prompt = r.Prompt,
        Message = r.Message
    };
    
    private class GoalRecord
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string UserId { get; set; } = "";
        public string Type { get; set; } = "";
        public string Priority { get; set; } = "";
        public string Status { get; set; } = "";
        public string Source { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string? DueAt { get; set; }
        public string? CompletedAt { get; set; }
        public string? Notes { get; set; }
    }
    
    private class GoalStepRecord
    {
        public int Id { get; set; }
        public int GoalId { get; set; }
        public int StepOrder { get; set; }
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Query { get; set; }
        public string? Prompt { get; set; }
        public string? Message { get; set; }
    }
    
    private class GoalEventRecord
    {
        public int Id { get; set; }
        public int GoalId { get; set; }
        public string EventType { get; set; } = "";
        public string? Details { get; set; }
        public string OccurredAt { get; set; } = "";
    }
}
