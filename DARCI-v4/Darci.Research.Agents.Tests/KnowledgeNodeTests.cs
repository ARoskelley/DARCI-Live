#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

public class KnowledgeNodeTests
{
    private sealed class FakePipeline : IKnowledgePipeline
    {
        private readonly KnowledgeResponse _response;
        public KnowledgeRequest? LastRequest;
        public FakePipeline(KnowledgeResponse response) => _response = response;
        public Task<KnowledgeResponse> RunAsync(KnowledgeRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }

    private static NodePacket Routed(IReadOnlyDictionary<string, string>? slots = null) =>
        NodePacket.Create("fix the failing build", capability: Capability.FillKnowledgeGap, slots: slots)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");

    [Fact]
    public async Task WritesStructuredResponseToSlots_AndSucceeds()
    {
        var response = new KnowledgeResponse
        {
            Answered = true,
            Confidence = Confidence.Of(0.7),
            DirectAnswer = "Use the correct table.",
            Findings = new[] { "interim starts at 0" },
        };
        var pipeline = new FakePipeline(response);
        var node = new KnowledgeNode(pipeline, NullLogger<KnowledgeNode>.Instance);

        var slots = new Dictionary<string, string> { [PacketSlots.Question] = "Damm table?", [PacketSlots.FailureContext] = "Expected 4 got 7" };
        var result = await node.HandleAsync(Routed(slots));

        Assert.Equal(NodeState.Succeeded, result.State);

        // Structured response is serialized into its slot and round-trips.
        var json = result.Payload.Slot(PacketSlots.KnowledgeResponse);
        Assert.False(string.IsNullOrWhiteSpace(json));
        var parsed = JsonSerializer.Deserialize<KnowledgeResponse>(json!);
        Assert.NotNull(parsed);
        Assert.Equal("Use the correct table.", parsed!.DirectAnswer);
        Assert.True(parsed.Answered);

        // Compat slots still present.
        Assert.False(string.IsNullOrWhiteSpace(result.Payload.Slot(PacketSlots.KnowledgeFindings)));
        Assert.Equal("0.7", result.Payload.Slot(PacketSlots.KnowledgeConfidence));

        // Request was built from the packet (question + failure context).
        Assert.Equal("Damm table?", pipeline.LastRequest!.Question);
        Assert.Equal("Expected 4 got 7", pipeline.LastRequest.FailureContext);
        Assert.Equal(KnowledgeKind.GapFill, pipeline.LastRequest.Kind);
    }

    [Fact]
    public async Task GapOnlyResponse_StillSucceeds_AtNodeLevel()
    {
        // "answered=false with explicit gaps" is a valid, useful result — the node still succeeds at
        // producing a structured response; the caller decides what to do with the gaps.
        var response = KnowledgeResponse.Unanswered("the exact quasigroup table is unknown");
        var node = new KnowledgeNode(new FakePipeline(response), NullLogger<KnowledgeNode>.Instance);

        var result = await node.HandleAsync(Routed());

        Assert.Equal(NodeState.Succeeded, result.State);
        var parsed = JsonSerializer.Deserialize<KnowledgeResponse>(result.Payload.Slot(PacketSlots.KnowledgeResponse)!);
        Assert.False(parsed!.Answered);
        Assert.Contains("the exact quasigroup table is unknown", parsed.Gaps);
    }

    [Fact]
    public async Task AnswerKnowledgeCapability_InfersFactLookupKind()
    {
        var pipeline = new FakePipeline(new KnowledgeResponse { Answered = true, DirectAnswer = "x" });
        var node = new KnowledgeNode(pipeline, NullLogger<KnowledgeNode>.Instance);

        var packet = NodePacket.Create("what is Damm?", capability: Capability.AnswerKnowledge)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");
        await node.HandleAsync(packet);

        Assert.Equal(KnowledgeKind.FactLookup, pipeline.LastRequest!.Kind);
    }
}
