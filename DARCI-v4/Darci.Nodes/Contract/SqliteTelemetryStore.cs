#nullable enable

using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// Durable telemetry (doc §6.3). Lives in its OWN database file (decision D6): different access pattern,
/// different retention, and telemetry volume has no business inside the knowledge graph or the trust ledger.
/// </summary>
public interface ITelemetryStore
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Persist one node-invocation record.</summary>
    Task RecordInvocationAsync(NodeTelemetryRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<NodeTelemetryRecord>> GetRecentAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>Every invocation under a goal (correlation root) — the causal chain of one piece of work.</summary>
    Task<IReadOnlyList<NodeTelemetryRecord>> GetByGoalAsync(string goalId, CancellationToken ct = default);

    /// <summary>Persist one brokered model call (the per-call grain behind an invocation's roll-up).</summary>
    Task RecordModelCallAsync(ModelCallRecord call, CancellationToken ct = default);

    /// <summary>Every model call made during one invocation.</summary>
    Task<IReadOnlyList<ModelCallRecord>> GetModelCallsAsync(string traceId, CancellationToken ct = default);
}

public sealed class SqliteTelemetryStore : ITelemetryStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteTelemetryStore> _logger;

    public SqliteTelemetryStore(string connectionString, ILogger<SqliteTelemetryStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // The full §6.3 shape. The model/token columns are nullable and stay empty until P2b.2 attributes
        // model calls to their invocation — defining them now avoids an ALTER on a live telemetry DB.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS node_invocations (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                trace_id TEXT NOT NULL,
                goal_id TEXT NOT NULL,
                node_id TEXT NOT NULL,
                capability TEXT NOT NULL,
                started_at TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                outcome INTEGER NOT NULL,
                confidence_score REAL NOT NULL,
                error_code TEXT NULL,
                blocked_on TEXT NULL,
                taint_level INTEGER NOT NULL DEFAULT 0,
                model_class TEXT NULL,
                model_resolved TEXT NULL,
                tokens_in INTEGER NULL,
                tokens_out INTEGER NULL,
                model_call_count INTEGER NULL,
                host_profile_id TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_node_invocations_goal ON node_invocations(goal_id, started_at);
            CREATE INDEX IF NOT EXISTS ix_node_invocations_trace ON node_invocations(trace_id);
            CREATE INDEX IF NOT EXISTS ix_node_invocations_node ON node_invocations(node_id, started_at);

            -- Per-call grain: §6.3 has room for one model per invocation, but a node makes many calls.
            -- The invocation row carries the roll-up; this carries the detail, linked by trace_id.
            CREATE TABLE IF NOT EXISTS model_calls (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                trace_id TEXT NOT NULL,
                goal_id TEXT NOT NULL,
                model_class TEXT NOT NULL,
                resolved_model TEXT NOT NULL,
                provider_kind TEXT NOT NULL,
                started_at TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                tokens_in INTEGER NOT NULL,
                tokens_out INTEGER NOT NULL,
                succeeded INTEGER NOT NULL,
                purpose TEXT NULL,
                error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_model_calls_trace ON model_calls(trace_id);
            CREATE INDEX IF NOT EXISTS ix_model_calls_goal ON model_calls(goal_id, started_at);
            CREATE INDEX IF NOT EXISTS ix_model_calls_model ON model_calls(resolved_model, started_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Telemetry store initialized (separate database).");
    }

    public async Task RecordInvocationAsync(NodeTelemetryRecord r, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO node_invocations
                (trace_id, goal_id, node_id, capability, started_at, duration_ms, outcome, confidence_score,
                 error_code, blocked_on, taint_level, model_class, model_resolved, tokens_in, tokens_out,
                 model_call_count, host_profile_id)
            VALUES ($trace, $goal, $node, $cap, $started, $dur, $outcome, $conf,
                    $err, $blocked, $taint, $mclass, $mresolved, $tin, $tout, $mcount, $profile)
            """;
        cmd.Parameters.AddWithValue("$trace", r.TraceId);
        cmd.Parameters.AddWithValue("$goal", r.GoalId);
        cmd.Parameters.AddWithValue("$node", r.NodeId);
        cmd.Parameters.AddWithValue("$cap", r.Capability);
        cmd.Parameters.AddWithValue("$started", ToIso(r.StartedAt));
        cmd.Parameters.AddWithValue("$dur", r.DurationMs);
        cmd.Parameters.AddWithValue("$outcome", (int)r.Outcome);
        cmd.Parameters.AddWithValue("$conf", r.Confidence.Score);
        cmd.Parameters.AddWithValue("$err", (object?)r.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$blocked", (object?)r.BlockedOn?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$taint", (int)r.TaintLevel);
        cmd.Parameters.AddWithValue("$mclass", (object?)r.ModelClass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mresolved", (object?)r.ModelResolved ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tin", r.TokensIn.HasValue ? r.TokensIn.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$tout", r.TokensOut.HasValue ? r.TokensOut.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$mcount", r.ModelCallCount.HasValue ? r.ModelCallCount.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$profile", (object?)r.HostProfileId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<NodeTelemetryRecord>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM node_invocations ORDER BY seq DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));
        return await ReadAll(cmd, ct);
    }

    public async Task<IReadOnlyList<NodeTelemetryRecord>> GetByGoalAsync(string goalId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM node_invocations WHERE goal_id = $goal ORDER BY seq";
        cmd.Parameters.AddWithValue("$goal", goalId);
        return await ReadAll(cmd, ct);
    }

    public async Task RecordModelCallAsync(ModelCallRecord c, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_calls
                (trace_id, goal_id, model_class, resolved_model, provider_kind, started_at, duration_ms,
                 tokens_in, tokens_out, succeeded, purpose, error)
            VALUES ($trace, $goal, $class, $model, $provider, $started, $dur, $tin, $tout, $ok, $purpose, $err)
            """;
        cmd.Parameters.AddWithValue("$trace", c.TraceId);
        cmd.Parameters.AddWithValue("$goal", c.GoalId);
        cmd.Parameters.AddWithValue("$class", c.ModelClass);
        cmd.Parameters.AddWithValue("$model", c.ResolvedModel);
        cmd.Parameters.AddWithValue("$provider", c.ProviderKind);
        cmd.Parameters.AddWithValue("$started", ToIso(c.StartedAt));
        cmd.Parameters.AddWithValue("$dur", c.DurationMs);
        cmd.Parameters.AddWithValue("$tin", c.TokensIn);
        cmd.Parameters.AddWithValue("$tout", c.TokensOut);
        cmd.Parameters.AddWithValue("$ok", c.Succeeded ? 1 : 0);
        cmd.Parameters.AddWithValue("$purpose", (object?)c.Purpose ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)c.Error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ModelCallRecord>> GetModelCallsAsync(string traceId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM model_calls WHERE trace_id = $trace ORDER BY seq";
        cmd.Parameters.AddWithValue("$trace", traceId);

        var list = new List<ModelCallRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var purposeOrd = reader.GetOrdinal("purpose");
            var errOrd = reader.GetOrdinal("error");
            list.Add(new ModelCallRecord(
                reader.GetString(reader.GetOrdinal("trace_id")),
                reader.GetString(reader.GetOrdinal("goal_id")),
                reader.GetString(reader.GetOrdinal("model_class")),
                reader.GetString(reader.GetOrdinal("resolved_model")),
                reader.GetString(reader.GetOrdinal("provider_kind")),
                FromIso(reader.GetString(reader.GetOrdinal("started_at"))),
                reader.GetInt64(reader.GetOrdinal("duration_ms")),
                reader.GetInt32(reader.GetOrdinal("tokens_in")),
                reader.GetInt32(reader.GetOrdinal("tokens_out")),
                reader.GetInt32(reader.GetOrdinal("succeeded")) != 0,
                reader.IsDBNull(purposeOrd) ? null : reader.GetString(purposeOrd),
                reader.IsDBNull(errOrd) ? null : reader.GetString(errOrd)));
        }
        return list;
    }

    private static async Task<IReadOnlyList<NodeTelemetryRecord>> ReadAll(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<NodeTelemetryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var errOrd = reader.GetOrdinal("error_code");
            var blockedOrd = reader.GetOrdinal("blocked_on");
            var mclassOrd = reader.GetOrdinal("model_class");
            var mresolvedOrd = reader.GetOrdinal("model_resolved");
            var tinOrd = reader.GetOrdinal("tokens_in");
            var toutOrd = reader.GetOrdinal("tokens_out");
            var mcountOrd = reader.GetOrdinal("model_call_count");
            var profileOrd = reader.GetOrdinal("host_profile_id");

            list.Add(new NodeTelemetryRecord(
                reader.GetString(reader.GetOrdinal("trace_id")),
                reader.GetString(reader.GetOrdinal("goal_id")),
                reader.GetString(reader.GetOrdinal("node_id")),
                reader.GetString(reader.GetOrdinal("capability")),
                FromIso(reader.GetString(reader.GetOrdinal("started_at"))),
                reader.GetInt64(reader.GetOrdinal("duration_ms")),
                (NodeOutcome)reader.GetInt32(reader.GetOrdinal("outcome")),
                new Confidence(reader.GetDouble(reader.GetOrdinal("confidence_score"))),
                reader.IsDBNull(errOrd) ? null : reader.GetString(errOrd),
                reader.IsDBNull(blockedOrd) ? null : Enum.Parse<DependencyKind>(reader.GetString(blockedOrd)),
                (TaintLevel)reader.GetInt32(reader.GetOrdinal("taint_level")))
            {
                ModelClass = reader.IsDBNull(mclassOrd) ? null : reader.GetString(mclassOrd),
                ModelResolved = reader.IsDBNull(mresolvedOrd) ? null : reader.GetString(mresolvedOrd),
                TokensIn = reader.IsDBNull(tinOrd) ? null : reader.GetInt32(tinOrd),
                TokensOut = reader.IsDBNull(toutOrd) ? null : reader.GetInt32(toutOrd),
                ModelCallCount = reader.IsDBNull(mcountOrd) ? null : reader.GetInt32(mcountOrd),
                HostProfileId = reader.IsDBNull(profileOrd) ? null : reader.GetString(profileOrd),
            });
        }
        return list;
    }

    private static string ToIso(DateTime v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromIso(string v) => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

/// <summary>
/// Writes telemetry to the store WITHOUT blocking the dispatch path: records go onto a bounded channel and a
/// single background writer drains them.
///
/// <para>Two deliberate properties: (1) the dispatcher is never slowed by a disk write, and (2) telemetry can
/// never take the application down — if the queue is full, records are DROPPED and the drop count is
/// reported, rather than blocking work or throwing. Losing a telemetry row is acceptable; stalling DARCI to
/// record one is not.</para>
/// </summary>
public sealed class TelemetryStoreSink : INodeTelemetrySink, IAsyncDisposable
{
    private readonly Channel<NodeTelemetryRecord> _queue;
    private readonly ITelemetryStore _store;
    private readonly ILogger<TelemetryStoreSink> _logger;
    private readonly Task _drain;
    private readonly CancellationTokenSource _stopping = new();

    private long _dropped;
    private int _disposed;

    public TelemetryStoreSink(ITelemetryStore store, ILogger<TelemetryStoreSink> logger, int capacity = 2048)
    {
        _store = store;
        _logger = logger;
        // FullMode.Wait + TryWrite is the combination that both never blocks AND reports saturation:
        // TryWrite returns false when the queue is full (it never waits), so a drop is COUNTABLE. With
        // DropWrite, TryWrite returns true while silently discarding the record — losing telemetry with no
        // way to know it happened, which is worse than losing it visibly.
        _queue = Channel.CreateBounded<NodeTelemetryRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Records dropped because the queue was saturated.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Record(NodeTelemetryRecord record)
    {
        if (!_queue.Writer.TryWrite(record)) Interlocked.Increment(ref _dropped);
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var record in _queue.Reader.ReadAllAsync(_stopping.Token))
            {
                try { await _store.RecordInvocationAsync(record, _stopping.Token); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // A telemetry write failure must never surface into the work path.
                    _logger.LogDebug(ex, "Telemetry write failed for trace {TraceId} (non-fatal).", record.TraceId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>Idempotent, as <see cref="IAsyncDisposable"/> requires: DI disposal plus an explicit
    /// <c>await using</c> would otherwise cancel an already-disposed token source and throw.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _queue.Writer.TryComplete();
        try { await _drain.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort flush */ }
        _stopping.Cancel();
        _stopping.Dispose();

        if (Dropped > 0)
            _logger.LogWarning("Telemetry sink dropped {Count} record(s) due to a saturated queue.", Dropped);
    }
}

/// <summary>
/// Queues per-call model telemetry to the store, with the same never-block / never-crash guarantees as
/// <see cref="TelemetryStoreSink"/>: model calls happen on the hot path, so this must never add latency.
/// </summary>
public sealed class ModelCallStoreSink : IModelCallSink, IAsyncDisposable
{
    private readonly Channel<ModelCallRecord> _queue;
    private readonly ITelemetryStore _store;
    private readonly ILogger<ModelCallStoreSink> _logger;
    private readonly Task _drain;
    private readonly CancellationTokenSource _stopping = new();

    private long _dropped;
    private int _disposed;

    public ModelCallStoreSink(ITelemetryStore store, ILogger<ModelCallStoreSink> logger, int capacity = 4096)
    {
        _store = store;
        _logger = logger;
        _queue = Channel.CreateBounded<ModelCallRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // with TryWrite: never blocks, and saturation is countable
            SingleReader = true,
        });
        _drain = Task.Run(DrainAsync);
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Record(ModelCallRecord call)
    {
        if (!_queue.Writer.TryWrite(call)) Interlocked.Increment(ref _dropped);
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var call in _queue.Reader.ReadAllAsync(_stopping.Token))
            {
                try { await _store.RecordModelCallAsync(call, _stopping.Token); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogDebug(ex, "Model-call telemetry write failed (non-fatal)."); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _queue.Writer.TryComplete();
        try { await _drain.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _stopping.Cancel();
        _stopping.Dispose();

        if (Dropped > 0)
            _logger.LogWarning("Model-call telemetry dropped {Count} record(s) due to a saturated queue.", Dropped);
    }
}

/// <summary>Fans one telemetry record out to several sinks (e.g. logs AND the store).</summary>
public sealed class CompositeNodeTelemetrySink : INodeTelemetrySink
{
    private readonly IReadOnlyList<INodeTelemetrySink> _sinks;

    public CompositeNodeTelemetrySink(IEnumerable<INodeTelemetrySink> sinks) => _sinks = sinks.ToList();

    public void Record(NodeTelemetryRecord record)
    {
        foreach (var sink in _sinks) sink.Record(record);
    }
}
