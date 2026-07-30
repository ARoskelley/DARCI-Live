#nullable enable

using System.Text.Json.Serialization;

namespace Darci.Nodes;

/// <summary>
/// The doc §5.3 request envelope: ONE invocation of ONE capability. Transient — it lives for a single call
/// and dies at <see cref="DeadlineAt"/>.
///
/// <para><b>This is NOT a replacement for <see cref="NodePacket"/> (F2: wraps, not replaces).</b>
/// <see cref="NodePacket"/> is the durable, stateful WORK RECORD the core owns (state machine, lease,
/// append-only log, and long-lived parking in <see cref="NodeState.AwaitingDependency"/>). An invocation is
/// the short message handed to a node. Collapsing the two would put a 30-second deadline in front of a human
/// gate that legitimately waits for days.</para>
///
/// <para><b>ADD-2 — correlation identity. <see cref="GoalId"/> IS THE CORRELATION ROOT</b>
/// (<see cref="NodePacket.CorrelationId"/>). It is the key the entire evidence loop hangs on: an innovated
/// entry's consumption link is recorded against it, and the outcome that later resolves that link is keyed
/// on it. <see cref="TraceId"/> is a FRESH per-invocation id for TELEMETRY ONLY and must NEVER be used as a
/// correlation key — doing so silently inerts the outcome-feedback loop (nothing would ever match).</para>
/// </summary>
public sealed record NodeInvocation
{
    public string EnvelopeVersion { get; init; } = NodeContractVersion.Current;

    /// <summary>Fresh per invocation. TELEMETRY CORRELATION ONLY — never an evidence/correlation key.</summary>
    public string TraceId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>THE CORRELATION ROOT (<see cref="NodePacket.CorrelationId"/>). See the ADD-2 note above.</summary>
    public string GoalId { get; init; } = "";

    /// <summary>The routable capability verb (a <see cref="Capabilities"/>-style `domain.action` string).</summary>
    public string Capability { get; init; } = "";

    public DateTime IssuedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Binding for THIS invocation only (doc §5.3). Expiry never aborts the work record — the core
    /// decides what a <see cref="NodeErrorCode.DeadlineExceeded"/> means for the packet.</summary>
    public DateTime DeadlineAt { get; init; } = DateTime.UtcNow.AddMinutes(5);

    public PrincipalRef Principal { get; init; } = PrincipalRef.Operator;

    /// <summary>Carried, not enforced in Phase 1 (trust/taint deferred).</summary>
    public TaintRef Taint { get; init; } = TaintRef.Clean;

    /// <summary>RESERVED / NO-OP in Phase 1 (ADD-5a). Nothing reads this until the brokers land.</summary>
    public BrokerRef Broker { get; init; } = BrokerRef.None;

    /// <summary>The originating goal/intent, never rewritten (mirrors <see cref="PacketPayload.Intent"/>).</summary>
    public string Intent { get; init; } = "";
    public string? SuccessCriteria { get; init; }

    /// <summary>
    /// The capability payload — string→string, mirroring <see cref="PacketPayload.Slots"/>. Deliberately kept
    /// trivially JSON-round-trippable (ADD-3) so an in-process node cannot come to depend on a payload shape
    /// that could not survive the out-of-process hop.
    /// </summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// PHASE-1 TRANSITIONAL SIDE-CHANNEL (F1a) — the live work record, for IN-PROCESS adapters only.
    /// <para>Today's nodes are packet-native (they call <c>packet.Transition(...)</c>/<c>WithSlot(...)</c> and
    /// return a transitioned packet), so Phase 1 hands them the real packet to preserve behavior exactly.
    /// It is <see cref="JsonIgnoreAttribute"/>d: it CANNOT cross a process boundary, so an out-of-process node
    /// can never see it. REMOVE when nodes are de-legacied (Phase 3+) — at which point payload-only is
    /// mandatory and this property should stop existing.</para>
    /// </summary>
    [JsonIgnore]
    public NodePacket? PacketRef { get; init; }

    public string? SessionId { get; init; }

    public bool IsExpired(DateTime nowUtc) => nowUtc > DeadlineAt;
}

/// <summary>The doc §5.3 response envelope, extended with Rev 0.1.1's <see cref="NodeOutcome.Blocked"/>.</summary>
public sealed record NodeResult
{
    public string EnvelopeVersion { get; init; } = NodeContractVersion.Current;

    /// <summary>MUST be echoed unchanged from the invocation (doc §5.3).</summary>
    public string TraceId { get; init; } = "";

    public NodeOutcome Outcome { get; init; } = NodeOutcome.Ok;

    /// <summary>Must be ≥ the request's taint (doc §5.3). Not enforced in Phase 1.</summary>
    public TaintRef Taint { get; init; } = TaintRef.Clean;

    /// <summary>Optional. Uses the unified <see cref="Darci.Nodes.Confidence"/> value type so it folds
    /// straight into the packet log; <see cref="Confidence.Unassessed"/> means "omitted", not "zero"
    /// (doc §5.3: "Omit rather than fabricate").</summary>
    public Confidence Confidence { get; init; } = Confidence.Unassessed;

    /// <summary>Slots the node produced, merged back onto the work record by the dispatcher.</summary>
    public IReadOnlyDictionary<string, string> Payload { get; init; } = new Dictionary<string, string>();

    /// <summary>Set iff <see cref="Outcome"/> is <see cref="NodeOutcome.Error"/>.</summary>
    public NodeError? Error { get; init; }

    /// <summary>Set iff <see cref="Outcome"/> is <see cref="NodeOutcome.Blocked"/> (Rev 0.1.1).</summary>
    public NodeDependency? Dependency { get; init; }

    /// <summary>Optional node-supplied telemetry detail. The core's base record cannot be suppressed (§6.3).</summary>
    public IReadOnlyDictionary<string, string>? TelemetryExtra { get; init; }

    /// <summary>
    /// PHASE-1 TRANSITIONAL SIDE-CHANNEL (F1a) — the packet the in-process node returned, so the dispatcher
    /// can fold the node's own transitions/log entries through unchanged. <see cref="JsonIgnoreAttribute"/>d;
    /// removed when nodes are de-legacied.
    /// </summary>
    [JsonIgnore]
    public NodePacket? PacketRef { get; init; }

    public static NodeResult Ok(string traceId, IReadOnlyDictionary<string, string>? payload = null, Confidence? confidence = null) =>
        new()
        {
            TraceId = traceId,
            Outcome = NodeOutcome.Ok,
            Payload = payload ?? new Dictionary<string, string>(),
            Confidence = confidence ?? Confidence.Unassessed,
        };

    public static NodeResult Failed(string traceId, NodeErrorCode code, string message) =>
        new() { TraceId = traceId, Outcome = NodeOutcome.Error, Error = NodeError.Of(code, message) };

    /// <summary>Bounded work complete, but the GOAL is now waiting on <paramref name="dependency"/> (Rev 0.1.1).</summary>
    public static NodeResult BlockedOn(string traceId, NodeDependency dependency) =>
        new() { TraceId = traceId, Outcome = NodeOutcome.Blocked, Dependency = dependency };
}
