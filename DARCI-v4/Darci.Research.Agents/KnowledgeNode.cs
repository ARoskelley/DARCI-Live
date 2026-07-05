#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// The KG / deep-research node as an <see cref="INode"/>, hardened in Phase 2 into a rigid black box:
/// a <see cref="KnowledgeRequest"/> in, a structured <see cref="KnowledgeResponse"/> out (decision 4).
/// When the response still has gaps, the node makes them actionable via the generic
/// <see cref="IGapHandler"/>: blocking gaps get an immediate fill (merged back in), non-blocking gaps
/// become deferred auto-goals for the living loop to learn from later.
///
/// Input slots:  <see cref="PacketSlots.Question"/> (falls back to Intent), <see cref="PacketSlots.FailureContext"/>,
///               <see cref="PacketSlots.KnowledgeKind"/>, <see cref="PacketSlots.Blocking"/>, <see cref="PacketSlots.GapFillDepth"/>.
/// Output slots: <see cref="PacketSlots.KnowledgeResponse"/> (structured JSON),
///               <see cref="PacketSlots.KnowledgeFindings"/> (compat), <see cref="PacketSlots.KnowledgeConfidence"/>.
/// </summary>
public sealed class KnowledgeNode : INode
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IKnowledgePipeline _pipeline;
    private readonly IGapHandler? _gapHandler;
    // Lazy to break the DI cycle: the router depends on this node, and this node routes to Innovation.
    private readonly Lazy<INodeRouter>? _innovationRouter;
    private readonly ILogger<KnowledgeNode> _logger;

    public KnowledgeNode(
        IKnowledgePipeline pipeline,
        ILogger<KnowledgeNode> logger,
        IGapHandler? gapHandler = null,
        Lazy<INodeRouter>? innovationRouter = null)
    {
        _pipeline = pipeline;
        _logger = logger;
        _gapHandler = gapHandler;
        _innovationRouter = innovationRouter;
    }

    public NodeId Id => NodeId.Knowledge;

    public IReadOnlySet<Capability> Capabilities { get; } =
        new HashSet<Capability> { Capability.AnswerKnowledge, Capability.FillKnowledgeGap };

    public async Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
    {
        packet = AdvanceToWorking(packet);

        var request = BuildRequest(packet);
        _logger.LogInformation("KnowledgeNode handling packet {Id}: {Q}",
            packet.Id, request.Question.Length > 100 ? request.Question[..100] : request.Question);

        try
        {
            var response = await _pipeline.RunAsync(request, ct);

            var blocking = string.Equals(packet.Payload.Slot(PacketSlots.Blocking), "true", StringComparison.OrdinalIgnoreCase);
            var topLevel = Depth(packet) == 0;

            // ESCALATE TO INNOVATION (§10): the KG + deep research are exhausted (not answered) on a
            // blocking, critical-path request. Innovation synthesizes a candidate hypothesis (or honestly
            // concludes it needs external input). Runs above the pipeline, KGMA-orchestrated.
            var innovationRan = false;
            if (topLevel && !response.Answered && response.Gaps.Count > 0 && blocking && _innovationRouter is not null)
            {
                (packet, response) = await EscalateToInnovationAsync(packet, request, response, ct);
                innovationRan = true;
            }

            // Make gaps actionable. After an innovation escalation, gaps are DEFERRED (learning), not
            // immediate-filled again (research already failed). Top level only (recursion guard).
            if (response.Gaps.Count > 0 && _gapHandler is not null && topLevel)
            {
                var gapBlocking = blocking && !innovationRan;
                var ctx = new GapContext(request.Question, request.Intent, response.Gaps, response.Confidence, gapBlocking, NodeId.Knowledge);
                var outcome = await _gapHandler.HandleAsync(packet, ctx, ct);
                packet = outcome.Packet;

                if (outcome.Disposition == GapDisposition.Immediate && outcome.FillResult is not null)
                {
                    var fill = ParseResponse(outcome.FillResult);
                    if (fill is not null) response = Merge(response, fill);
                }
            }

            packet = packet
                .WithSlot(PacketSlots.KnowledgeResponse, JsonSerializer.Serialize(response, JsonOpts))
                .WithSlot(PacketSlots.KnowledgeFindings, response.ToReviewText())
                .WithSlot(PacketSlots.KnowledgeConfidence, response.Confidence.Score.ToString("0.###"));

            var decision = response.Answered
                ? "Structured knowledge response produced (answered)."
                : $"Structured knowledge response produced with {response.Gaps.Count} gap(s).";

            return packet.Transition(NodeId.Knowledge, NodeState.Succeeded,
                decision, confidence: response.Confidence, success: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "KnowledgeNode pipeline failed for packet {Id}.", packet.Id);
            return packet.Transition(NodeId.Knowledge, NodeState.Failed,
                "Knowledge pipeline threw.", success: false, error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Merge an immediate-fill response into the original: union the structured content, take the
    /// fill's residual gaps + answered/confidence (it is the more recent, focused result).</summary>
    internal static KnowledgeResponse Merge(KnowledgeResponse original, KnowledgeResponse fill) => original with
    {
        DirectAnswer = string.IsNullOrWhiteSpace(fill.DirectAnswer) ? original.DirectAnswer : fill.DirectAnswer,
        Findings = original.Findings.Concat(fill.Findings).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Steps = original.Steps.Concat(fill.Steps).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Examples = original.Examples.Concat(fill.Examples).ToList(),
        Citations = original.Citations.Concat(fill.Citations).ToList(),
        Gaps = fill.Gaps,
        Answered = fill.Answered || original.Answered,
        Confidence = fill.Confidence.IsAssessed ? fill.Confidence : original.Confidence,
    };

    private static KnowledgeResponse? ParseResponse(NodePacket packet)
    {
        var json = packet.Payload.Slot(PacketSlots.KnowledgeResponse);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<KnowledgeResponse>(json); }
        catch { return null; }
    }

    /// <summary>
    /// Routes a child <see cref="Capability.Innovate"/> packet (KGMA-orchestrated, shared correlation) and
    /// folds the resulting proposal into the response: an <see cref="ProposalStatus.Unsolvable"/> conclusion
    /// adds its required external inputs as gaps; a synthesized hypothesis is added as a clearly-marked,
    /// unverified low-confidence candidate finding. The answer stays unanswered — it's a hypothesis.
    /// </summary>
    private async Task<(NodePacket Packet, KnowledgeResponse Response)> EscalateToInnovationAsync(
        NodePacket packet, KnowledgeRequest request, KnowledgeResponse response, CancellationToken ct)
    {
        var knownFacts = response.Findings.ToList();
        if (!string.IsNullOrWhiteSpace(response.DirectAnswer)) knownFacts.Add(response.DirectAnswer);

        var slots = new Dictionary<string, string>
        {
            [PacketSlots.Question] = request.Question,
            [PacketSlots.InnovationGaps] = JsonSerializer.Serialize(response.Gaps, JsonOpts),
            [PacketSlots.InnovationKnownFacts] = JsonSerializer.Serialize(knownFacts, JsonOpts),
        };
        if (!string.IsNullOrWhiteSpace(request.FailureContext))
            slots[PacketSlots.FailureContext] = request.FailureContext!;

        var child = NodePacket.Create(request.Intent, capability: Capability.Innovate,
            correlationId: packet.CorrelationId, slots: slots);

        packet = packet.Transition(NodeId.Knowledge, NodeState.Working,
            "Escalated to innovation node (KG + deep research exhausted).", confidence: response.Confidence);

        InnovationProposal? proposal = null;
        try
        {
            var result = await _innovationRouter!.Value.DispatchAsync(child, ct);
            var json = result.Payload.Slot(PacketSlots.InnovationProposal);
            if (!string.IsNullOrWhiteSpace(json)) proposal = JsonSerializer.Deserialize<InnovationProposal>(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Innovation escalation failed (non-fatal).");
        }

        if (proposal is null) return (packet, response);

        if (proposal.Status == ProposalStatus.Unsolvable)
        {
            var gaps = response.Gaps
                .Concat(proposal.RequiredExternalInputs.Select(x => $"needs external input: {x}"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return (packet, response with { Gaps = gaps });
        }

        var findings = response.Findings
            .Append($"[innovated hypothesis — unverified, low confidence]: {proposal.Hypothesis}")
            .ToList();
        return (packet, response with { Findings = findings });
    }

    private static int Depth(NodePacket packet)
        => int.TryParse(packet.Payload.Slot(PacketSlots.GapFillDepth), out var d) ? d : 0;

    private static KnowledgeRequest BuildRequest(NodePacket packet)
    {
        var question = packet.Payload.Slot(PacketSlots.Question) ?? packet.Payload.Intent;
        var failureContext = packet.Payload.Slot(PacketSlots.FailureContext);

        var kind = KnowledgeKind.GapFill;
        var kindSlot = packet.Payload.Slot(PacketSlots.KnowledgeKind);
        if (!string.IsNullOrWhiteSpace(kindSlot) && Enum.TryParse<KnowledgeKind>(kindSlot, ignoreCase: true, out var parsed))
            kind = parsed;
        else if (packet.RequestedCapability == Capability.AnswerKnowledge)
            kind = KnowledgeKind.FactLookup;

        return new KnowledgeRequest(question, packet.Payload.Intent, failureContext, kind);
    }

    private static NodePacket AdvanceToWorking(NodePacket packet)
    {
        if (packet.State == NodeState.Routed)
            packet = packet.Transition(NodeId.Knowledge, NodeState.Accepted, "Knowledge node accepted request");
        if (packet.State == NodeState.Accepted)
            packet = packet.Transition(NodeId.Knowledge, NodeState.Working, "Researching", leaseFor: TimeSpan.FromMinutes(3));
        return packet;
    }
}
