#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// The per-invocation telemetry record (doc §6.3). Emitted by the CORE (the dispatcher), never by the node,
/// so it cannot be skipped or faked.
///
/// <para><b>On the model fields:</b> §6.3 lists <c>model_class</c>/<c>model_resolved</c>/<c>tokens_*</c> as
/// singular fields, but a node may make MANY model calls in one invocation. So these carry the ROLL-UP for
/// the invocation — summed tokens, the count of calls, and the dominant class/model — while per-call detail
/// lives in its own table (P2b.2). They are nullable because an invocation that called no model has no model
/// facts to report, which is different from "zero tokens".</para>
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
    TaintLevel TaintLevel = TaintLevel.Clean)
{
    /// <summary>Dominant model class across this invocation's model calls (null if it made none).</summary>
    public string? ModelClass { get; init; }

    /// <summary>The concrete model that class resolved to — what ACTUALLY ran.</summary>
    public string? ModelResolved { get; init; }

    /// <summary>Summed prompt tokens across this invocation's model calls.</summary>
    public int? TokensIn { get; init; }

    /// <summary>Summed completion tokens.</summary>
    public int? TokensOut { get; init; }

    /// <summary>How many model calls this invocation made — the number that turns tokens into a rate.</summary>
    public int? ModelCallCount { get; init; }

    /// <summary>Which host profile was active, so cross-host telemetry stays comparable.</summary>
    public string? HostProfileId { get; init; }
}

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
            "duration_ms={DurationMs} outcome={Outcome} confidence={Confidence} error={ErrorCode} blocked_on={BlockedOn} " +
            "model={Model} calls={ModelCalls} tokens_in={TokensIn} tokens_out={TokensOut}",
            r.TraceId, r.GoalId, r.NodeId, r.Capability, r.DurationMs, r.Outcome,
            r.Confidence.IsAssessed ? r.Confidence.Score.ToString("0.###") : "—",
            r.ErrorCode ?? "—", r.BlockedOn?.ToString() ?? "—",
            r.ModelResolved ?? "—", r.ModelCallCount?.ToString() ?? "—",
            r.TokensIn?.ToString() ?? "—", r.TokensOut?.ToString() ?? "—");
}

/// <summary>Discards telemetry — for tests and for hosts that opt out.</summary>
public sealed class NullNodeTelemetrySink : INodeTelemetrySink
{
    public static NullNodeTelemetrySink Instance { get; } = new();
    public void Record(NodeTelemetryRecord record) { }
}
