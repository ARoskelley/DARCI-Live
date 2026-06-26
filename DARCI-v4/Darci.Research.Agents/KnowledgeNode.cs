#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// The KG / deep-research node as an <see cref="INode"/> (Step B). Wraps the existing
/// <see cref="IDeepResearchOrchestrator"/> behind the packet contract so other nodes reach it by
/// routing a packet (capability <see cref="Capability.FillKnowledgeGap"/> /
/// <see cref="Capability.AnswerKnowledge"/>) instead of calling it directly — the packet-routed
/// replacement for the hardcoded eng→research handoff.
///
/// Input slots:  <see cref="PacketSlots.Question"/> (required), <see cref="PacketSlots.FailureContext"/> (optional).
/// Output slots: <see cref="PacketSlots.KnowledgeFindings"/>, <see cref="PacketSlots.KnowledgeConfidence"/>.
/// Runs to completion synchronously (research is bounded), then returns the packet in a terminal state.
/// </summary>
public sealed class KnowledgeNode : INode
{
    private readonly IDeepResearchOrchestrator _research;
    private readonly ILogger<KnowledgeNode> _logger;

    public KnowledgeNode(IDeepResearchOrchestrator research, ILogger<KnowledgeNode> logger)
    {
        _research = research;
        _logger = logger;
    }

    public NodeId Id => NodeId.Knowledge;

    public IReadOnlySet<Capability> Capabilities { get; } =
        new HashSet<Capability> { Capability.AnswerKnowledge, Capability.FillKnowledgeGap };

    public async Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
    {
        // Drive Routed → Accepted → Working.
        packet = AdvanceToWorking(packet);

        var question = packet.Payload.Slot(PacketSlots.Question) ?? packet.Payload.Intent;
        var failureContext = packet.Payload.Slot(PacketSlots.FailureContext);
        var fullQuestion = string.IsNullOrWhiteSpace(failureContext)
            ? question
            : $"{question}\n\nObserved failure context:\n{failureContext}";

        _logger.LogInformation("KnowledgeNode handling packet {Id}: {Q}",
            packet.Id, question.Length > 100 ? question[..100] : question);

        try
        {
            var outcome = await _research.RunDeepResearchAsync(fullQuestion, "DARCI", ct);

            if (outcome.IsSuccess && !string.IsNullOrWhiteSpace(outcome.FinalAnswer))
            {
                packet = packet
                    .WithSlot(PacketSlots.KnowledgeFindings, outcome.FinalAnswer)
                    .WithSlot(PacketSlots.KnowledgeConfidence, outcome.Confidence.Score.ToString("0.###"));

                return packet.Transition(NodeId.Knowledge, NodeState.Succeeded,
                    "Research synthesized findings.",
                    confidence: outcome.Confidence,
                    success: true);
            }

            return packet.Transition(NodeId.Knowledge, NodeState.Failed,
                "Research produced no usable findings.",
                confidence: outcome.Confidence,
                success: false,
                error: outcome.Error ?? "No findings.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "KnowledgeNode research failed for packet {Id}.", packet.Id);
            return packet.Transition(NodeId.Knowledge, NodeState.Failed,
                "Research threw.", success: false, error: $"{ex.GetType().Name}: {ex.Message}");
        }
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
