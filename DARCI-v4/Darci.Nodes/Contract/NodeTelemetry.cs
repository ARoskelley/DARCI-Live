#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// The per-invocation telemetry record (doc §6.3), MINIMAL for Phase 1 (ADD-5c).
///
/// <para>Present now: <see cref="TraceId"/>, <see cref="GoalId"/>, <see cref="NodeId"/>,
/// <see cref="Capability"/>, <see cref="DurationMs"/>, <see cref="Outcome"/>, <see cref="Confidence"/>.
/// Deferred to Phase 2 with the model broker: <c>model_class</c>, <c>model_resolved</c>, <c>tokens_in</c>,
/// <c>tokens_out</c>, <c>host_profile_id</c> — the core cannot know those until inference is brokered.</para>
///
/// <para>Emitted by the CORE (the dispatcher), never by the node, so it cannot be skipped or faked (§6.3).</para>
/// </summary>
public sealed record NodeTelemetryRecord(
    string TraceId,
    string GoalId,
    string NodeId,
    string Capability,
    DateTime StartedAt,
    long DurationMs,
    NodeOutcome Outcome,
    Confidence Confidence,
    string? ErrorCode = null,
    DependencyKind? BlockedOn = null,
    TaintLevel TaintLevel = TaintLevel.Clean);

/// <summary>Where telemetry records go. Phase 1 ships a logging sink; Phase 2 adds a durable store
/// (doc D6: local SQLite/Postgres, SEPARATE from the knowledge graph).</summary>
public interface INodeTelemetrySink
{
    void Record(NodeTelemetryRecord record);
}

/// <summary>Phase 1 sink: structured log lines. Cheap, always on, no schema to migrate yet.</summary>
public sealed class LoggingNodeTelemetrySink : INodeTelemetrySink
{
    private readonly ILogger<LoggingNodeTelemetrySink> _logger;

    public LoggingNodeTelemetrySink(ILogger<LoggingNodeTelemetrySink> logger) => _logger = logger;

    public void Record(NodeTelemetryRecord r) =>
        _logger.LogInformation(
            "node-telemetry trace={TraceId} goal={GoalId} node={NodeId} capability={Capability} " +
            "duration_ms={DurationMs} outcome={Outcome} confidence={Confidence} error={ErrorCode} blocked_on={BlockedOn}",
            r.TraceId, r.GoalId, r.NodeId, r.Capability, r.DurationMs, r.Outcome,
            r.Confidence.IsAssessed ? r.Confidence.Score.ToString("0.###") : "—",
            r.ErrorCode ?? "—", r.BlockedOn?.ToString() ?? "—");
}

/// <summary>Discards telemetry — for tests and for hosts that opt out.</summary>
public sealed class NullNodeTelemetrySink : INodeTelemetrySink
{
    public static NullNodeTelemetrySink Instance { get; } = new();
    public void Record(NodeTelemetryRecord record) { }
}
