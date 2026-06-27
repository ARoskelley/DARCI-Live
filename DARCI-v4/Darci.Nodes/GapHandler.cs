#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>Tunable knobs for gap handling.</summary>
public sealed class GapHandlerOptions
{
    /// <summary>How many immediate-fill hops are allowed before a gap is deferred instead (recursion guard).</summary>
    public int MaxImmediateFillDepth { get; init; } = 1;
}

/// <summary>What the handler did with a set of gaps.</summary>
public enum GapDisposition { None, Immediate, Deferred }

/// <summary>The gaps to act on, plus the signals that drive the immediate-vs-deferred decision.</summary>
public sealed record GapContext(
    string Question,
    string Intent,
    IReadOnlyList<string> Gaps,
    Confidence Confidence,
    bool Blocking,
    NodeId OriginNode);

/// <summary>Result of handling gaps: the (logged) origin packet, what was done, the immediate fill packet
/// (if any), and the gap records produced.</summary>
public sealed record GapHandlingOutcome(
    NodePacket Packet,
    GapDisposition Disposition,
    NodePacket? FillResult,
    IReadOnlyList<GapRecord> Gaps);

/// <summary>
/// Makes gaps actionable (Tinman's gap-driven action). Generic — any node that reports gaps routes them
/// here, not just coding. Two paths, chosen as an explicit, logged decision:
///   • IMMEDIATE — the gap is on the critical path of the active work (blocking) and we have fill budget
///     left: route a focused knowledge/research packet now to resolve it before continuing.
///   • DEFERRED — the gap isn't blocking: persist it and create an auto-goal the living loop can pick up
///     later, so the system learns about it over time.
/// </summary>
public interface IGapHandler
{
    Task<GapHandlingOutcome> HandleAsync(NodePacket packet, GapContext context, CancellationToken ct = default);
}

public sealed class GapHandler : IGapHandler
{
    private readonly IGapStore _store;
    // Lazy to break the DI cycle: a node depends on the handler, the handler routes via the router,
    // and the router depends on the nodes.
    private readonly Lazy<INodeRouter> _router;
    private readonly GapHandlerOptions _options;
    private readonly IGapGoalSink? _goalSink;
    private readonly ILogger<GapHandler> _logger;

    public GapHandler(
        IGapStore store,
        Lazy<INodeRouter> router,
        GapHandlerOptions options,
        ILogger<GapHandler> logger,
        IGapGoalSink? goalSink = null)
    {
        _store = store;
        _router = router;
        _options = options;
        _logger = logger;
        _goalSink = goalSink;
    }

    public async Task<GapHandlingOutcome> HandleAsync(NodePacket packet, GapContext context, CancellationToken ct = default)
    {
        if (context.Gaps.Count == 0)
            return new GapHandlingOutcome(packet, GapDisposition.None, null, Array.Empty<GapRecord>());

        var depth = ParseDepth(packet);

        // ── IMMEDIATE: blocking gap on the critical path, with fill budget remaining ──
        if (context.Blocking && depth < _options.MaxImmediateFillDepth)
        {
            var records = new List<GapRecord>();
            foreach (var gap in context.Gaps)
            {
                var rec = NewRecord(packet, context, gap, GapStatus.Filling);
                await _store.AddAsync(rec, ct);
                records.Add(rec);
            }

            var combined = string.Join("; ", context.Gaps);
            packet = packet.Transition(context.OriginNode, NodeState.Working,
                $"Gap handling: IMMEDIATE fill of {context.Gaps.Count} gap(s) on the critical path (blocking, depth {depth}).",
                confidence: context.Confidence);

            var child = NodePacket.Create(
                intent: context.Intent,
                capability: Capability.FillKnowledgeGap,
                correlationId: packet.CorrelationId,
                slots: new Dictionary<string, string>
                {
                    [PacketSlots.Question] = $"Resolve specifically: {combined}\n(regarding: {context.Question})",
                    [PacketSlots.FailureContext] = combined,
                    [PacketSlots.GapFillDepth] = (depth + 1).ToString(),
                    [PacketSlots.Blocking] = "true",
                });

            _logger.LogInformation("Gap handler: immediate fill for {Count} blocking gap(s) at depth {Depth}.",
                context.Gaps.Count, depth);
            var fill = await _router.Value.DispatchAsync(child, ct);
            return new GapHandlingOutcome(packet, GapDisposition.Immediate, fill, records);
        }

        // ── DEFERRED: persist + auto-goal so the living loop learns about it later ──
        var deferred = new List<GapRecord>();
        var goalIds = new List<string>();
        foreach (var gap in context.Gaps)
        {
            var rec = NewRecord(packet, context, gap, GapStatus.Deferred);
            await _store.AddAsync(rec, ct);

            if (_goalSink is not null)
            {
                var goalId = await SafeCreateGoalAsync(rec, ct);
                if (!string.IsNullOrEmpty(goalId))
                {
                    rec = rec with { GoalId = goalId, Status = GapStatus.GoalCreated, UpdatedAt = DateTime.UtcNow };
                    await _store.UpdateAsync(rec, ct);
                    goalIds.Add(goalId!);
                }
            }
            deferred.Add(rec);
        }

        var goalNote = goalIds.Count > 0 ? $" → auto-goal(s) [{string.Join(", ", goalIds)}]" : " (no goal sink)";
        packet = packet.Transition(context.OriginNode, NodeState.Working,
            $"Gap handling: DEFERRED {context.Gaps.Count} non-blocking gap(s) for later learning{goalNote}.",
            confidence: context.Confidence);

        _logger.LogInformation("Gap handler: deferred {Count} gap(s); created {Goals} goal(s).",
            deferred.Count, goalIds.Count);
        return new GapHandlingOutcome(packet, GapDisposition.Deferred, null, deferred);
    }

    private async Task<string?> SafeCreateGoalAsync(GapRecord rec, CancellationToken ct)
    {
        try { return await _goalSink!.CreateGoalForGapAsync(rec, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Gap goal sink failed for gap {Id} (gap still persisted).", rec.Id);
            return null;
        }
    }

    private static GapRecord NewRecord(NodePacket packet, GapContext ctx, string gap, string status) => new()
    {
        CorrelationId = packet.CorrelationId,
        OriginPacketId = packet.Id,
        OriginNode = ctx.OriginNode,
        Question = ctx.Question,
        Intent = ctx.Intent,
        Missing = gap,
        Confidence = ctx.Confidence,
        Status = status,
    };

    private static int ParseDepth(NodePacket packet)
        => int.TryParse(packet.Payload.Slot(PacketSlots.GapFillDepth), out var d) ? d : 0;
}
