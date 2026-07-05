#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// The innovation / ideation node as an <see cref="INode"/>. KGMA escalates to it (capability
/// <see cref="Capability.Innovate"/>) when the KG + deep research are exhausted. Phase D replaced the Phase B
/// single pass with the bounded diverse-candidate <see cref="IInnovationLoop"/>: the loop runs multiple
/// generate→screen→falsify cycles (generator ≠ critic ≠ reviewer inside it) and returns either the winning
/// candidate or an honest <see cref="ProposalStatus.Unsolvable"/>. This node stays thin: it builds the
/// request, runs the loop, and — for a winner — persists it as a capped <b>Innovated</b> entry (always IsLow,
/// NOT promoted) and files a human promotion proposal. Trust only rises via a human event (invariant §0a).
///
/// Input slots:  <see cref="PacketSlots.Question"/>, <see cref="PacketSlots.FailureContext"/>,
///               <see cref="PacketSlots.InnovationGaps"/>, <see cref="PacketSlots.InnovationKnownFacts"/>.
/// Output slot:  <see cref="PacketSlots.InnovationProposal"/> (structured JSON).
///
/// NOT here (design-only): validation campaigns / tooling proposals + above-cap promotion pathway (Phase E).
/// </summary>
public sealed class InnovationNode : INode
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IInnovationLoop _loop;              // bounded diverse-candidate governor (Phase D)
    private readonly IInnovatedKnowledgeStore _store;
    private readonly IProposalStore? _proposals;        // human gate (Phase C) — file, never auto-decide
    private readonly ILogger<InnovationNode> _logger;

    public InnovationNode(
        IInnovationLoop loop,
        IInnovatedKnowledgeStore store,
        ILogger<InnovationNode> logger,
        IProposalStore? proposals = null)
    {
        _loop = loop;
        _store = store;
        _logger = logger;
        _proposals = proposals;
    }

    public NodeId Id => NodeId.Innovation;

    public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.Innovate };

    public async Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
    {
        packet = AdvanceToWorking(packet);
        var request = BuildRequest(packet);
        _logger.LogInformation("InnovationNode loop for packet {Id}: {Q}",
            packet.Id, request.Question.Length > 100 ? request.Question[..100] : request.Question);

        try
        {
            // 1) Run the bounded diverse-candidate loop (generate → screen → falsify, progress-governed).
            //    It returns the vetted winner (capped Innovated + Plausibility) or an honest Unsolvable.
            var proposal = await _loop.RunAsync(request, ct) with { CorrelationId = packet.CorrelationId };

            if (proposal.Status != ProposalStatus.Unsolvable && !string.IsNullOrWhiteSpace(proposal.Hypothesis))
            {
                // 2) Persist as an unconfirmed Innovated hypothesis (capped, logged). NOT promoted.
                var record = await PersistAsync(packet, request, proposal, ct);

                // 3) Human gate (Phase C): file a promotion proposal so the hypothesis is surfaced for the
                //    human's attention — never sits silent, never auto-promotes. Filing does NOT park this
                //    packet: the capped hypothesis is usable now; promotion is an asynchronous human choice.
                await FileProposalAsync(record, proposal, ct);
            }

            packet = packet.WithSlot(PacketSlots.InnovationProposal, JsonSerializer.Serialize(proposal, JsonOpts));

            var decision = proposal.Status == ProposalStatus.Unsolvable
                ? $"No synthesis of known information solves this; {proposal.RequiredExternalInputs.Count} external input(s) required."
                : "Innovated hypothesis synthesized, reviewed, and persisted (unconfirmed, capped).";

            return packet.Transition(NodeId.Innovation, NodeState.Succeeded, decision,
                confidence: proposal.Confidence, success: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "InnovationNode failed for packet {Id}.", packet.Id);
            return packet.Transition(NodeId.Innovation, NodeState.Failed,
                "Innovation synthesis threw.", success: false, error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<InnovatedKnowledgeRecord> PersistAsync(NodePacket packet, InnovationRequest request, InnovationProposal proposal, CancellationToken ct)
    {
        var record = new InnovatedKnowledgeRecord
        {
            CorrelationId = packet.CorrelationId,
            OriginPacketId = packet.Id,
            Hypothesis = proposal.Hypothesis,
            Topic = request.Question,
            Intent = request.Intent,
            CitedFactIds = proposal.Reasoning.SelectMany(r => r.CitedFacts).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Provenance = Provenance.Innovated,     // never higher — invariant §0a
            Confidence = proposal.Confidence,       // store re-clamps anyway
        };
        await _store.AddAsync(record, ct);
        _logger.LogInformation("Persisted innovated hypothesis {Id} (Innovated, capped {Score:0.##}).",
            record.Id, record.Confidence.Score);
        return record;
    }

    private async Task FileProposalAsync(InnovatedKnowledgeRecord record, InnovationProposal proposal, CancellationToken ct)
    {
        if (_proposals is null) return;
        try
        {
            var proposalRecord = new HumanProposal
            {
                CorrelationId = record.CorrelationId,
                Kind = HumanProposalKind.PromoteInnovated,
                SubjectId = record.Id,
                TargetProvenance = Provenance.HumanApproved,
                Title = $"Promote innovated hypothesis: {Truncate(record.Hypothesis, 80)}",
                Summary = record.Hypothesis,
                JustificationJson = JsonSerializer.Serialize(new
                {
                    hypothesis = record.Hypothesis,
                    topic = record.Topic,
                    reasoning = proposal.Reasoning,
                    assumptions = proposal.Assumptions,
                    plausibility = proposal.Plausibility,
                }, JsonOpts),
                // No parked packet: the capped hypothesis returns and is usable now; promotion is async.
            };
            await _proposals.AddAsync(proposalRecord, ct);
            _logger.LogInformation("Filed promotion proposal {PId} for innovated {Id}.", proposalRecord.Id, record.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Filing promotion proposal failed (non-fatal).");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static InnovationRequest BuildRequest(NodePacket packet)
    {
        var question = packet.Payload.Slot(PacketSlots.Question) ?? packet.Payload.Intent;
        return new InnovationRequest(
            question,
            packet.Payload.Intent,
            packet.Payload.Slot(PacketSlots.FailureContext),
            ReadStrings(packet.Payload.Slot(PacketSlots.InnovationGaps)),
            ReadStrings(packet.Payload.Slot(PacketSlots.InnovationKnownFacts)));
    }

    private static List<string> ReadStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); } catch { return new(); }
    }

    private static NodePacket AdvanceToWorking(NodePacket packet)
    {
        if (packet.State == NodeState.Routed)
            packet = packet.Transition(NodeId.Innovation, NodeState.Accepted, "Innovation node accepted request");
        if (packet.State == NodeState.Accepted)
            packet = packet.Transition(NodeId.Innovation, NodeState.Working, "Synthesizing", leaseFor: TimeSpan.FromMinutes(3));
        return packet;
    }
}
