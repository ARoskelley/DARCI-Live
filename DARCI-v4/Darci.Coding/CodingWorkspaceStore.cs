#nullable enable

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

public sealed class CodingWorkspaceStore : ICodingWorkspaceStore
{
    private readonly string _connectionString;
    private readonly ILogger<CodingWorkspaceStore> _logger;

    public CodingWorkspaceStore(string connectionString, ILogger<CodingWorkspaceStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS coding_workspaces (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                root_path TEXT NOT NULL,
                created_by TEXT NOT NULL,
                status TEXT NOT NULL,
                summary TEXT NOT NULL,
                tags_json TEXT NOT NULL,
                detected_languages_json TEXT NOT NULL,
                detected_commands_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                last_indexed_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS coding_files (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                extension TEXT NOT NULL,
                kind TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                is_text INTEGER NOT NULL,
                last_modified_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_coding_files_workspace_path
                ON coding_files(workspace_id, relative_path);

            CREATE TABLE IF NOT EXISTS coding_file_embeddings (
                file_id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                embedding_json TEXT NOT NULL,
                computed_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_coding_file_embeddings_workspace
                ON coding_file_embeddings(workspace_id);

            CREATE TABLE IF NOT EXISTS coding_command_runs (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                task_id TEXT NOT NULL DEFAULT '',
                working_directory TEXT NOT NULL,
                command TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                display_command TEXT NOT NULL,
                exit_code INTEGER NULL,
                timed_out INTEGER NOT NULL,
                was_allowed INTEGER NOT NULL,
                stdout_tail TEXT NOT NULL,
                stderr_tail TEXT NOT NULL,
                error TEXT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_coding_command_runs_workspace_started
                ON coding_command_runs(workspace_id, started_at);

            CREATE INDEX IF NOT EXISTS ix_coding_command_runs_task
                ON coding_command_runs(task_id, started_at);

            CREATE TABLE IF NOT EXISTS coding_tasks (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                prompt TEXT NOT NULL,
                success_criteria TEXT NOT NULL,
                created_by TEXT NOT NULL,
                status TEXT NOT NULL,
                plan TEXT NOT NULL,
                last_context_package_json TEXT NOT NULL,
                plan_generated_by TEXT NOT NULL DEFAULT 'template',
                plan_model TEXT NOT NULL DEFAULT '',
                current_step_index INTEGER NOT NULL DEFAULT 0,
                last_step_result TEXT NOT NULL DEFAULT '',
                roadblock_research TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_coding_tasks_workspace_updated
                ON coding_tasks(workspace_id, updated_at);

            CREATE TABLE IF NOT EXISTS coding_checkpoints (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                commit_sha TEXT NOT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_coding_checkpoints_workspace_task
                ON coding_checkpoints(workspace_id, task_id, created_at);
            """;

        await cmd.ExecuteNonQueryAsync(ct);

        // Migrate pre-existing tables to add new columns if needed.
        await TryMigrateAsync(conn, "ALTER TABLE coding_tasks ADD COLUMN plan_generated_by TEXT NOT NULL DEFAULT 'template'", ct);
        await TryMigrateAsync(conn, "ALTER TABLE coding_tasks ADD COLUMN plan_model TEXT NOT NULL DEFAULT ''", ct);
        await TryMigrateAsync(conn, "ALTER TABLE coding_tasks ADD COLUMN current_step_index INTEGER NOT NULL DEFAULT 0", ct);
        await TryMigrateAsync(conn, "ALTER TABLE coding_tasks ADD COLUMN last_step_result TEXT NOT NULL DEFAULT ''", ct);
        await TryMigrateAsync(conn, "ALTER TABLE coding_tasks ADD COLUMN roadblock_research TEXT NOT NULL DEFAULT ''", ct);
        await TryMigrateAsync(conn, "ALTER TABLE coding_command_runs ADD COLUMN task_id TEXT NOT NULL DEFAULT ''", ct);

        _logger.LogInformation("Coding workspace store initialized.");
    }

    private static async Task TryMigrateAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException)
        {
            // Column already exists — safe to ignore.
        }
    }

    public async Task UpsertWorkspaceAsync(CodingWorkspace workspace, IReadOnlyList<CodingFileEntry> files, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO coding_workspaces
                (id, name, root_path, created_by, status, summary, tags_json,
                 detected_languages_json, detected_commands_json, created_at, last_indexed_at)
                VALUES
                ($id, $name, $root_path, $created_by, $status, $summary, $tags_json,
                 $detected_languages_json, $detected_commands_json, $created_at, $last_indexed_at)
                """;
            cmd.Parameters.AddWithValue("$id", workspace.Id);
            cmd.Parameters.AddWithValue("$name", workspace.Name);
            cmd.Parameters.AddWithValue("$root_path", workspace.RootPath);
            cmd.Parameters.AddWithValue("$created_by", workspace.CreatedBy);
            cmd.Parameters.AddWithValue("$status", workspace.Status);
            cmd.Parameters.AddWithValue("$summary", workspace.Summary);
            cmd.Parameters.AddWithValue("$tags_json", ToJson(workspace.Tags));
            cmd.Parameters.AddWithValue("$detected_languages_json", ToJson(workspace.DetectedLanguages));
            cmd.Parameters.AddWithValue("$detected_commands_json", ToJson(workspace.DetectedCommands));
            cmd.Parameters.AddWithValue("$created_at", ToIso(workspace.CreatedAt));
            cmd.Parameters.AddWithValue("$last_indexed_at", workspace.LastIndexedAt is null ? DBNull.Value : ToIso(workspace.LastIndexedAt.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM coding_files WHERE workspace_id = $workspace_id";
            cmd.Parameters.AddWithValue("$workspace_id", workspace.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var file in files)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO coding_files
                (id, workspace_id, relative_path, extension, kind, size_bytes, is_text, last_modified_utc)
                VALUES
                ($id, $workspace_id, $relative_path, $extension, $kind, $size_bytes, $is_text, $last_modified_utc)
                """;
            cmd.Parameters.AddWithValue("$id", file.Id);
            cmd.Parameters.AddWithValue("$workspace_id", file.WorkspaceId);
            cmd.Parameters.AddWithValue("$relative_path", file.RelativePath);
            cmd.Parameters.AddWithValue("$extension", file.Extension);
            cmd.Parameters.AddWithValue("$kind", file.Kind);
            cmd.Parameters.AddWithValue("$size_bytes", file.SizeBytes);
            cmd.Parameters.AddWithValue("$is_text", file.IsText ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_modified_utc", ToIso(file.LastModifiedUtc));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CodingWorkspace>> GetWorkspacesAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM coding_workspaces ORDER BY created_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var results = new List<CodingWorkspace>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapWorkspace(reader));
        }

        return results;
    }

    public async Task<CodingWorkspace?> GetWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM coding_workspaces WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", workspaceId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapWorkspace(reader) : null;
    }

    public async Task<IReadOnlyList<CodingFileEntry>> GetFilesAsync(string workspaceId, int limit = 500, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM coding_files
            WHERE workspace_id = $workspace_id
            ORDER BY relative_path
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));

        var results = new List<CodingFileEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapFile(reader));
        }

        return results;
    }

    public async Task AddCommandRunAsync(CodingCommandRun run, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO coding_command_runs
            (id, workspace_id, task_id, working_directory, command, arguments_json, display_command,
             exit_code, timed_out, was_allowed, stdout_tail, stderr_tail, error, started_at, completed_at)
            VALUES
            ($id, $workspace_id, $task_id, $working_directory, $command, $arguments_json, $display_command,
             $exit_code, $timed_out, $was_allowed, $stdout_tail, $stderr_tail, $error, $started_at, $completed_at)
            """;
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$workspace_id", run.WorkspaceId);
        cmd.Parameters.AddWithValue("$task_id", run.TaskId);
        cmd.Parameters.AddWithValue("$working_directory", run.WorkingDirectory);
        cmd.Parameters.AddWithValue("$command", run.Command);
        cmd.Parameters.AddWithValue("$arguments_json", ToJson(run.Arguments));
        cmd.Parameters.AddWithValue("$display_command", run.DisplayCommand);
        cmd.Parameters.AddWithValue("$exit_code", run.ExitCode is null ? DBNull.Value : run.ExitCode.Value);
        cmd.Parameters.AddWithValue("$timed_out", run.TimedOut ? 1 : 0);
        cmd.Parameters.AddWithValue("$was_allowed", run.WasAllowed ? 1 : 0);
        cmd.Parameters.AddWithValue("$stdout_tail", run.StdoutTail);
        cmd.Parameters.AddWithValue("$stderr_tail", run.StderrTail);
        cmd.Parameters.AddWithValue("$error", run.Error is null ? DBNull.Value : run.Error);
        cmd.Parameters.AddWithValue("$started_at", ToIso(run.StartedAt));
        cmd.Parameters.AddWithValue("$completed_at", ToIso(run.CompletedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CodingCommandRun>> GetCommandRunsAsync(string workspaceId, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM coding_command_runs
            WHERE workspace_id = $workspace_id
            ORDER BY started_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var runs = new List<CodingCommandRun>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runs.Add(MapCommandRun(reader));
        }

        return runs;
    }

    public async Task<IReadOnlyList<CodingCommandRun>> GetRecentCommandRunsForTaskAsync(string taskId, int limit = 10, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM coding_command_runs
            WHERE task_id = $task_id
            ORDER BY started_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$task_id", taskId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        var runs = new List<CodingCommandRun>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runs.Add(MapCommandRun(reader));
        }

        return runs;
    }

    public async Task UpsertFileEmbeddingAsync(string fileId, string workspaceId, float[] embedding, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO coding_file_embeddings (file_id, workspace_id, embedding_json, computed_at)
            VALUES ($file_id, $workspace_id, $embedding_json, $computed_at)
            """;
        cmd.Parameters.AddWithValue("$file_id", fileId);
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("$embedding_json", JsonSerializer.Serialize(embedding));
        cmd.Parameters.AddWithValue("$computed_at", ToIso(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, float[]>> GetFileEmbeddingsAsync(string workspaceId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_id, embedding_json FROM coding_file_embeddings WHERE workspace_id = $workspace_id";
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);

        var result = new Dictionary<string, float[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fileId = reader.GetString(0);
            try
            {
                var arr = JsonSerializer.Deserialize<float[]>(reader.GetString(1));
                if (arr is { Length: > 0 })
                {
                    result[fileId] = arr;
                }
            }
            catch { /* corrupt row — skip */ }
        }

        return result;
    }

    public async Task AddCheckpointAsync(CodingCheckpoint checkpoint, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO coding_checkpoints (id, workspace_id, task_id, commit_sha, message, created_at)
            VALUES ($id, $workspace_id, $task_id, $commit_sha, $message, $created_at)
            """;
        cmd.Parameters.AddWithValue("$id", checkpoint.Id);
        cmd.Parameters.AddWithValue("$workspace_id", checkpoint.WorkspaceId);
        cmd.Parameters.AddWithValue("$task_id", checkpoint.TaskId);
        cmd.Parameters.AddWithValue("$commit_sha", checkpoint.CommitSha);
        cmd.Parameters.AddWithValue("$message", checkpoint.Message);
        cmd.Parameters.AddWithValue("$created_at", ToIso(checkpoint.CreatedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<CodingCheckpoint?> GetLatestCheckpointAsync(string workspaceId, string taskId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM coding_checkpoints
            WHERE workspace_id = $workspace_id AND task_id = $task_id
            ORDER BY created_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("$task_id", taskId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new CodingCheckpoint
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            WorkspaceId = reader.GetString(reader.GetOrdinal("workspace_id")),
            TaskId = reader.GetString(reader.GetOrdinal("task_id")),
            CommitSha = reader.GetString(reader.GetOrdinal("commit_sha")),
            Message = reader.GetString(reader.GetOrdinal("message")),
            CreatedAt = FromIso(reader.GetString(reader.GetOrdinal("created_at")))
        };
    }

    public async Task AddTaskAsync(CodingTaskRecord task, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO coding_tasks
            (id, workspace_id, prompt, success_criteria, created_by, status, plan,
             last_context_package_json, plan_generated_by, plan_model,
             current_step_index, last_step_result, roadblock_research, created_at, updated_at)
            VALUES
            ($id, $workspace_id, $prompt, $success_criteria, $created_by, $status, $plan,
             $last_context_package_json, $plan_generated_by, $plan_model,
             $current_step_index, $last_step_result, $roadblock_research, $created_at, $updated_at)
            """;
        BindTask(cmd, task);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateTaskAsync(CodingTaskRecord task, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE coding_tasks
            SET workspace_id = $workspace_id,
                prompt = $prompt,
                success_criteria = $success_criteria,
                created_by = $created_by,
                status = $status,
                plan = $plan,
                last_context_package_json = $last_context_package_json,
                plan_generated_by = $plan_generated_by,
                plan_model = $plan_model,
                current_step_index = $current_step_index,
                last_step_result = $last_step_result,
                roadblock_research = $roadblock_research,
                created_at = $created_at,
                updated_at = $updated_at
            WHERE id = $id
            """;
        BindTask(cmd, task);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<CodingTaskRecord?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM coding_tasks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", taskId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapTask(reader) : null;
    }

    public async Task<IReadOnlyList<CodingTaskRecord>> GetTasksAsync(string? workspaceId = null, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(workspaceId)
            ? "SELECT * FROM coding_tasks ORDER BY updated_at DESC LIMIT $limit"
            : "SELECT * FROM coding_tasks WHERE workspace_id = $workspace_id ORDER BY updated_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            cmd.Parameters.AddWithValue("$workspace_id", workspaceId);
        }

        var tasks = new List<CodingTaskRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tasks.Add(MapTask(reader));
        }

        return tasks;
    }

    private static void BindTask(SqliteCommand cmd, CodingTaskRecord task)
    {
        cmd.Parameters.AddWithValue("$id", task.Id);
        cmd.Parameters.AddWithValue("$workspace_id", task.WorkspaceId);
        cmd.Parameters.AddWithValue("$prompt", task.Prompt);
        cmd.Parameters.AddWithValue("$success_criteria", task.SuccessCriteria);
        cmd.Parameters.AddWithValue("$created_by", task.CreatedBy);
        cmd.Parameters.AddWithValue("$status", task.Status);
        cmd.Parameters.AddWithValue("$plan", task.Plan);
        cmd.Parameters.AddWithValue("$last_context_package_json", task.LastContextPackageJson);
        cmd.Parameters.AddWithValue("$plan_generated_by", task.PlanGeneratedBy);
        cmd.Parameters.AddWithValue("$plan_model", task.PlanModel);
        cmd.Parameters.AddWithValue("$current_step_index", task.CurrentStepIndex);
        cmd.Parameters.AddWithValue("$last_step_result", task.LastStepResult);
        cmd.Parameters.AddWithValue("$roadblock_research", task.RoadblockResearch);
        cmd.Parameters.AddWithValue("$created_at", ToIso(task.CreatedAt));
        cmd.Parameters.AddWithValue("$updated_at", ToIso(task.UpdatedAt));
    }

    private static CodingWorkspace MapWorkspace(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        RootPath = reader.GetString(reader.GetOrdinal("root_path")),
        CreatedBy = reader.GetString(reader.GetOrdinal("created_by")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        Summary = reader.GetString(reader.GetOrdinal("summary")),
        Tags = FromJsonArray(reader.GetString(reader.GetOrdinal("tags_json"))),
        DetectedLanguages = FromJsonArray(reader.GetString(reader.GetOrdinal("detected_languages_json"))),
        DetectedCommands = FromJsonArray(reader.GetString(reader.GetOrdinal("detected_commands_json"))),
        CreatedAt = FromIso(reader.GetString(reader.GetOrdinal("created_at"))),
        LastIndexedAt = reader.IsDBNull(reader.GetOrdinal("last_indexed_at"))
            ? null
            : FromIso(reader.GetString(reader.GetOrdinal("last_indexed_at")))
    };

    private static CodingFileEntry MapFile(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        WorkspaceId = reader.GetString(reader.GetOrdinal("workspace_id")),
        RelativePath = reader.GetString(reader.GetOrdinal("relative_path")),
        Extension = reader.GetString(reader.GetOrdinal("extension")),
        Kind = reader.GetString(reader.GetOrdinal("kind")),
        SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
        IsText = reader.GetInt32(reader.GetOrdinal("is_text")) == 1,
        LastModifiedUtc = FromIso(reader.GetString(reader.GetOrdinal("last_modified_utc")))
    };

    private static CodingCommandRun MapCommandRun(SqliteDataReader reader)
    {
        var taskIdOrd = reader.GetOrdinal("task_id");
        return new CodingCommandRun
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            WorkspaceId = reader.GetString(reader.GetOrdinal("workspace_id")),
            TaskId = reader.IsDBNull(taskIdOrd) ? "" : reader.GetString(taskIdOrd),
            WorkingDirectory = reader.GetString(reader.GetOrdinal("working_directory")),
            Command = reader.GetString(reader.GetOrdinal("command")),
            Arguments = FromJsonArray(reader.GetString(reader.GetOrdinal("arguments_json"))),
            DisplayCommand = reader.GetString(reader.GetOrdinal("display_command")),
            ExitCode = reader.IsDBNull(reader.GetOrdinal("exit_code")) ? null : reader.GetInt32(reader.GetOrdinal("exit_code")),
            TimedOut = reader.GetInt32(reader.GetOrdinal("timed_out")) == 1,
            WasAllowed = reader.GetInt32(reader.GetOrdinal("was_allowed")) == 1,
            StdoutTail = reader.GetString(reader.GetOrdinal("stdout_tail")),
            StderrTail = reader.GetString(reader.GetOrdinal("stderr_tail")),
            Error = reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
            StartedAt = FromIso(reader.GetString(reader.GetOrdinal("started_at"))),
            CompletedAt = FromIso(reader.GetString(reader.GetOrdinal("completed_at")))
        };
    }

    private static CodingTaskRecord MapTask(SqliteDataReader reader)
    {
        var pgbOrd = reader.GetOrdinal("plan_generated_by");
        var pmOrd = reader.GetOrdinal("plan_model");
        var csiOrd = reader.GetOrdinal("current_step_index");
        var lsrOrd = reader.GetOrdinal("last_step_result");
        var rrOrd = reader.GetOrdinal("roadblock_research");
        return new CodingTaskRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            WorkspaceId = reader.GetString(reader.GetOrdinal("workspace_id")),
            Prompt = reader.GetString(reader.GetOrdinal("prompt")),
            SuccessCriteria = reader.GetString(reader.GetOrdinal("success_criteria")),
            CreatedBy = reader.GetString(reader.GetOrdinal("created_by")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            Plan = reader.GetString(reader.GetOrdinal("plan")),
            LastContextPackageJson = reader.GetString(reader.GetOrdinal("last_context_package_json")),
            PlanGeneratedBy = reader.IsDBNull(pgbOrd) ? "template" : reader.GetString(pgbOrd),
            PlanModel = reader.IsDBNull(pmOrd) ? "" : reader.GetString(pmOrd),
            CurrentStepIndex = reader.IsDBNull(csiOrd) ? 0 : reader.GetInt32(csiOrd),
            LastStepResult = reader.IsDBNull(lsrOrd) ? "" : reader.GetString(lsrOrd),
            RoadblockResearch = reader.IsDBNull(rrOrd) ? "" : reader.GetString(rrOrd),
            CreatedAt = FromIso(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = FromIso(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    private static string ToJson(string[] value) => JsonSerializer.Serialize(value);

    private static string[] FromJsonArray(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string ToIso(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime FromIso(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
