#nullable enable

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// THE ONE DISPATCH POINT (doc §9 Phase 1). Every node-capability invocation goes through here: it projects
/// the durable work record into a <see cref="NodeInvocation"/>, calls the node's
/// <see cref="INodeAdapter"/>, emits telemetry, and folds the <see cref="NodeResult"/> back onto the record.
///
/// <para><b>Scope note:</b> "one dispatch point" means one point for NODE-CAPABILITY invocations. Model calls
/// (ModelRouter/Ollama) are the MODEL BROKER's job and are deliberately out of scope until Phase 2.</para>
///
/// <para><b>What the dispatcher must never do</b> (the C3 firewall, pinned by tests in SU4): it does not park,
/// unpark, or abort a work record, and an invocation's <see cref="NodeInvocation.DeadlineAt"/> has no
/// relationship to the record's lease. Long-lived waits belong to the core's goal/task lifecycle (doc §3) —
/// the human gate legitimately waits for days, which no per-invocation deadline may cut short.</para>
/// </summary>
public sealed class NodeDispatcher
{
    private readonly INodeTelemetrySink _telemetry;
    private readonly ILogger<NodeDispatcher> _logger;

    public NodeDispatcher(ILogger<NodeDispatcher> logger, INodeTelemetrySink? telemetry = null)
    {
        _logger = logger;
        _telemetry = telemetry ?? NullNodeTelemetrySink.Instance;
    }

    /// <summary>
    /// Invoke <paramref name="registration"/> for <paramref name="packet"/> and return the resulting work
    /// record. <paramref name="capability"/> is the resolved routing key (may be empty when the packet was
    /// routed by explicit address without declaring one).
    /// </summary>
    public async Task<NodePacket> DispatchAsync(
        NodeRegistration registration,
        NodePacket packet,
        string capability,
        CancellationToken ct = default)
    {
        var invocation = Project(packet, registration, capability);
        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        NodeResult result;
        try
        {
            result = await registration.Adapter.InvokeAsync(invocation, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // The adapter threw. Report it as an INTERNAL error result; the CALLER decides the record's fate,
            // preserving the router's existing exception handling exactly.
            _telemetry.Record(new NodeTelemetryRecord(
                invocation.TraceId, invocation.GoalId, registration.NodeId, capability,
                startedAt, sw.ElapsedMilliseconds, NodeOutcome.Error, Confidence.Unassessed,
                ErrorCode: NodeErrorCode.Internal.ToString()));
            throw;
        }

        sw.Stop();

        // MUST echo the trace id unchanged (doc §5.3). A mismatch is a node bug; log and carry on with ours.
        if (!string.Equals(result.TraceId, invocation.TraceId, StringComparison.Ordinal))
            _logger.LogWarning(
                "Node {NodeId} did not echo trace_id ({Expected} → {Actual}); telemetry correlation for this " +
                "invocation is unreliable.", registration.NodeId, invocation.TraceId, result.TraceId);

        _telemetry.Record(new NodeTelemetryRecord(
            invocation.TraceId, invocation.GoalId, registration.NodeId, capability,
            startedAt, sw.ElapsedMilliseconds, result.Outcome, result.Confidence,
            ErrorCode: result.Error?.WireCode,
            BlockedOn: result.Dependency?.Kind,
            TaintLevel: result.Taint.Level));

        return Fold(packet, registration, result);
    }

    /// <summary>
    /// Project the work record into an invocation envelope.
    ///
    /// <para><b>ADD-2:</b> <see cref="NodeInvocation.GoalId"/> is the packet's <see cref="NodePacket.CorrelationId"/>
    /// — the correlation root the whole evidence loop keys on. <see cref="NodeInvocation.TraceId"/> is FRESH per
    /// invocation and is telemetry-only; it must never be used as a correlation key.</para>
    /// </summary>
    internal static NodeInvocation Project(NodePacket packet, NodeRegistration registration, string capability)
    {
        var descriptor = registration.Manifest.Capabilities.FirstOrDefault(c => c.Name == capability);
        var deadlineMs = descriptor?.DeadlineMs ?? 300_000;

        return new NodeInvocation
        {
            EnvelopeVersion = NodeContractVersion.Current,
            TraceId = Guid.NewGuid().ToString("N"),     // per-invocation, telemetry ONLY
            GoalId = packet.CorrelationId,              // the correlation root — the durable key
            Capability = capability,
            IssuedAt = DateTime.UtcNow,
            // Per-invocation budget only. Deliberately NOT derived from, and never able to shorten, the
            // record's lease — see the C3 firewall note on this class.
            DeadlineAt = DateTime.UtcNow.AddMilliseconds(deadlineMs),
            Principal = PrincipalRef.Operator,
            Taint = TaintRef.Clean,                     // carried, not enforced in Phase 1
            Broker = BrokerRef.None,                    // reserved no-op until Phase 2
            Intent = packet.Payload.Intent,
            SuccessCriteria = packet.Payload.SuccessCriteria,
            Payload = packet.Payload.Slots,
            PacketRef = packet,                         // F1a transitional in-process side-channel
        };
    }

    /// <summary>
    /// Fold a result back onto the work record.
    ///
    /// <para>Legacy in-process nodes already transitioned and logged the record themselves, so their returned
    /// packet IS the authoritative result and passes through unchanged — that is what makes this sub-unit
    /// behavior-preserving. The payload-only branch below is the future (Phase 3+) path for nodes that no
    /// longer touch the packet.</para>
    /// </summary>
    private NodePacket Fold(NodePacket original, NodeRegistration registration, NodeResult result)
    {
        if (result.PacketRef is not null) return result.PacketRef;

        var nodeId = CapabilityKey.ToLegacyNode(registration.NodeId) ?? NodeId.Orchestrator;
        var packet = original;

        foreach (var (key, value) in result.Payload)
            packet = packet.WithSlot(key, value);

        switch (result.Outcome)
        {
            case NodeOutcome.Blocked:
                var detail = result.Dependency?.Detail ?? "awaiting an external dependency";
                return packet.State == NodeState.AwaitingDependency
                    ? packet
                    : packet.ParkAwaitingDependency(nodeId, detail, result.Confidence);

            case NodeOutcome.Error:
                return packet.Transition(nodeId, NodeState.Failed,
                    result.Error?.Message ?? "node reported an error",
                    confidence: result.Confidence, success: false,
                    error: $"{result.Error?.WireCode}: {result.Error?.Message}");

            default:
                if (packet.State == NodeState.Routed)
                    packet = packet.Transition(nodeId, NodeState.Accepted, "accepted");
                if (packet.State == NodeState.Accepted)
                    packet = packet.Transition(nodeId, NodeState.Working, "working");
                return packet.Transition(nodeId, NodeState.Succeeded, "completed",
                    confidence: result.Confidence, success: true);
        }
    }
}
