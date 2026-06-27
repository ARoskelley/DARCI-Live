#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// The KG / deep-research node as an <see cref="INode"/>, hardened in Phase 2 into a rigid black box:
/// a <see cref="KnowledgeRequest"/> in, a structured <see cref="KnowledgeResponse"/> out (decision 4).
/// The pipeline (admin/KG → review → escalate → compile → review) runs inside; the node only translates
/// between the packet and the contract.
///
/// Input slots:  <see cref="PacketSlots.Question"/> (falls back to Intent), <see cref="PacketSlots.FailureContext"/>,
///               <see cref="PacketSlots.KnowledgeKind"/> (optional).
/// Output slots: <see cref="PacketSlots.KnowledgeResponse"/> (structured JSON),
///               <see cref="PacketSlots.KnowledgeFindings"/> (compat rendering),
///               <see cref="PacketSlots.KnowledgeConfidence"/>.
/// </summary>
public sealed class KnowledgeNode : INode
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IKnowledgePipeline _pipeline;
    private readonly ILogger<KnowledgeNode> _logger;

    public KnowledgeNode(IKnowledgePipeline pipeline, ILogger<KnowledgeNode> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
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

            packet = packet
                .WithSlot(PacketSlots.KnowledgeResponse, JsonSerializer.Serialize(response, JsonOpts))
                .WithSlot(PacketSlots.KnowledgeFindings, response.ToReviewText())
                .WithSlot(PacketSlots.KnowledgeConfidence, response.Confidence.Score.ToString("0.###"));

            // The node SUCCEEDS at producing a structured response even when that response reports gaps —
            // "answered=false with explicit gaps" is a useful, trustworthy result for the caller.
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

    private static KnowledgeRequest BuildRequest(NodePacket packet)
    {
        var question = packet.Payload.Slot(PacketSlots.Question) ?? packet.Payload.Intent;
        var failureContext = packet.Payload.Slot(PacketSlots.FailureContext);

        // Kind: explicit slot wins; else infer from the requested capability.
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
