using System.Text.Json;
using Darci.Shared;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Brain;

/// <summary>
/// SQLite-backed ring buffer for DQN experience replay.
///
/// Each row stores one (state, action, reward, next_state, is_terminal) tuple
/// serialised to JSON. When the buffer exceeds <see cref="MaxCapacity"/>,
/// the oldest rows are evicted so the buffer always reflects recent experience.
///
/// Random sampling is done via SQLite's ORDER BY RANDOM() which is fast enough
/// for our buffer sizes (< 100k rows).
/// </summary>
public sealed class ExperienceBuffer : IExperienceBuffer
{
    private readonly string _connectionString;
    private readonly ILogger<ExperienceBuffer> _logger;

    /// <summary>Maximum number of experiences stored before eviction begins.</summary>
    public int MaxCapacity { get; }

    private static readonly JsonSerializerOptions _json = new()
    {
        // Compact JSON — these arrays can be large
        WriteIndented = false
    };

    public ExperienceBuffer(
        string connectionString,
        int maxCapacity = 10_000,
        ILogger<ExperienceBuffer>? logger = null)
    {
        _connectionString = connectionString;
        MaxCapacity = maxCapacity;
        _logger = logger ?? NullLogger<ExperienceBuffer>.Instance;
    }

    public async Task InitializeAsync()
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS experiences (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                state       TEXT    NOT NULL,
                action      INTEGER NOT NULL,
                reward      REAL    NOT NULL,
                next_state  TEXT    NOT NULL,
                is_terminal INTEGER NOT NULL DEFAULT 0,
                timestamp   TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS decision_log (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                state_vector     TEXT    NOT NULL,
                action_chosen    INTEGER NOT NULL,
                network_decision INTEGER NOT NULL DEFAULT 0,
                confidence       REAL,
                timestamp        TEXT    NOT NULL
            );
            """);

        _logger.LogInformation("ExperienceBuffer initialised (capacity={Cap})", MaxCapacity);
    }

    public async Task StoreAsync(Experience experience)
    {
        using var conn = OpenConnection();

        await conn.ExecuteAsync("""
            INSERT INTO experiences (state, action, reward, next_state, is_terminal, timestamp)
            VALUES (@State, @Action, @Reward, @NextState, @IsTerminal, @Timestamp)
            """,
            new
            {
                State      = Serialize(experience.State),
                experience.Action,
                experience.Reward,
                NextState  = Serialize(experience.NextState),
                IsTerminal = experience.IsTerminal ? 1 : 0,
                Timestamp  = experience.Timestamp.ToString("O")
            });

        await EvictIfOverCapacityAsync(conn);
    }

    public async Task<IReadOnlyList<Experience>> SampleAsync(int batchSize)
    {
        using var conn = OpenConnection();

        var rows = await conn.QueryAsync("""
            SELECT id, state, action, reward, next_state, is_terminal, timestamp
            FROM   experiences
            ORDER  BY RANDOM()
            LIMIT  @BatchSize
            """,
            new { BatchSize = batchSize });

        return rows.Select(r => new Experience
        {
            Id         = (long)r.id,
            State      = Deserialize((string)r.state),
            Action     = (int)(long)r.action,
            Reward     = (float)(double)r.reward,
            NextState  = Deserialize((string)r.next_state),
            IsTerminal = (long)r.is_terminal == 1,
            Timestamp  = DateTime.Parse((string)r.timestamp)
        }).ToList();
    }

    public async Task<int> CountAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM experiences");
    }

    public async Task ClearAsync()
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM experiences");
        _logger.LogWarning("ExperienceBuffer cleared — all experiences deleted");
    }

    // =========================================================
    // Decision Log helpers (used by the instrumented Decision.cs)
    // =========================================================

    /// <summary>
    /// Append a decision log entry. Called by the instrumented Decide() in Core
    /// for every action taken, regardless of whether the network or fallback decided.
    /// </summary>
    public async Task LogDecisionAsync(DecisionLog log)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO decision_log (state_vector, action_chosen, network_decision, confidence, timestamp)
            VALUES (@StateVector, @ActionChosen, @NetworkDecision, @Confidence, @Timestamp)
            """,
            new
            {
                StateVector     = Serialize(log.StateVector),
                log.ActionChosen,
                NetworkDecision = log.NetworkDecision ? 1 : 0,
                log.Confidence,
                Timestamp       = log.Timestamp.ToString("O")
            });
    }

    /// <summary>
    /// Retrieve the most recent <paramref name="limit"/> decision log entries,
    /// newest first. Used for shadow-mode disagreement analysis.
    /// </summary>
    public async Task<IReadOnlyList<DecisionLog>> GetRecentDecisionsAsync(int limit = 100)
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync("""
            SELECT id, state_vector, action_chosen, network_decision, confidence, timestamp
            FROM   decision_log
            ORDER  BY id DESC
            LIMIT  @Limit
            """,
            new { Limit = limit });

        return rows.Select(r => new DecisionLog
        {
            Id              = (long)r.id,
            StateVector     = Deserialize((string)r.state_vector),
            ActionChosen    = (int)(long)r.action_chosen,
            NetworkDecision = (long)r.network_decision == 1,
            Confidence      = r.confidence is double d ? (float)d : null,
            Timestamp       = DateTime.Parse((string)r.timestamp)
        }).ToList();
    }

    // =========================================================
    // Private helpers
    // =========================================================

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private async Task EvictIfOverCapacityAsync(SqliteConnection conn)
    {
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM experiences");
        if (count <= MaxCapacity) return;

        var excess = count - MaxCapacity;
        await conn.ExecuteAsync($"""
            DELETE FROM experiences
            WHERE id IN (
                SELECT id FROM experiences ORDER BY id ASC LIMIT {excess}
            )
            """);

        _logger.LogDebug("Evicted {N} oldest experiences (buffer at capacity)", excess);
    }

    private static string Serialize(float[] arr) =>
        JsonSerializer.Serialize(arr, _json);

    private static float[] Deserialize(string json) =>
        JsonSerializer.Deserialize<float[]>(json, _json) ?? [];
}
