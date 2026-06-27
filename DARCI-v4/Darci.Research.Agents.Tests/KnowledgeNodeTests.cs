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

    private sealed class FakeGapHandler : IGapHandler
    {
        private readonly GapHandlingOutcome? _override;
        public GapContext? LastContext;
        public bool Invoked;
        public FakeGapHandler(GapHandlingOutcome? outcomeOverride = null) => _override = outcomeOverride;

        public Task<GapHandlingOutcome> HandleAsync(NodePacket packet, GapContext context, CancellationToken ct = default)
        {
            Invoked = true;
            LastContext = context;
            var outcome = _override is null
                ? new GapHandlingOutcome(packet, GapDisposition.Deferred, null, Array.Empty<GapRecord>())
                : _override with { Packet = packet };
            return Task.FromResult(outcome);
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

    [Fact]
    public async Task ResponseWithGaps_InvokesGapHandler_WithBlockingFlagFromSlot()
    {
        var response = KnowledgeResponse.Unanswered("the exact table is unknown");
        var handler = new FakeGapHandler();
        var node = new KnowledgeNode(new FakePipeline(response), NullLogger<KnowledgeNode>.Instance, handler);

        var slots = new Dictionary<string, string> { [PacketSlots.Blocking] = "true" };
        await node.HandleAsync(Routed(slots));

        Assert.True(handler.Invoked);
        Assert.True(handler.LastContext!.Blocking);
        Assert.Contains("the exact table is unknown", handler.LastContext.Gaps);
    }

    [Fact]
    public async Task NonBlockingByDefault_WhenNoBlockingSlot()
    {
        var handler = new FakeGapHandler();
        var node = new KnowledgeNode(new FakePipeline(KnowledgeResponse.Unanswered("gap")), NullLogger<KnowledgeNode>.Instance, handler);

        await node.HandleAsync(Routed());

        Assert.True(handler.Invoked);
        Assert.False(handler.LastContext!.Blocking);   // absent slot → deferred path
    }

    [Fact]
    public async Task ImmediateFillResult_IsMergedIntoResponse()
    {
        var original = KnowledgeResponse.Unanswered("need the table");
        var fill = new KnowledgeResponse
        {
            Answered = true,
            Confidence = Confidence.Of(0.8),
            DirectAnswer = "Here is the table.",
            Findings = new[] { "row 0 = identity" },
            Gaps = Array.Empty<string>(),
        };
        var fillPacket = NodePacket.Create("fill", capability: Capability.FillKnowledgeGap)
            .Transition(NodeId.Knowledge, NodeState.Routed, "r")
            .WithSlot(PacketSlots.KnowledgeResponse, JsonSerializer.Serialize(fill));

        var handler = new FakeGapHandler(new GapHandlingOutcome(
            Packet: null!, GapDisposition.Immediate, fillPacket, Array.Empty<GapRecord>()));
        var node = new KnowledgeNode(new FakePipeline(original), NullLogger<KnowledgeNode>.Instance, handler);

        var result = await node.HandleAsync(Routed(new Dictionary<string, string> { [PacketSlots.Blocking] = "true" }));

        var merged = JsonSerializer.Deserialize<KnowledgeResponse>(result.Payload.Slot(PacketSlots.KnowledgeResponse)!);
        Assert.True(merged!.Answered);                              // fill answered it
        Assert.Contains("row 0 = identity", merged.Findings);      // fill findings merged in
        Assert.Empty(merged.Gaps);                                  // residual gaps from the fill
    }

    [Fact]
    public async Task NestedFill_Depth1_SkipsGapHandling()
    {
        var handler = new FakeGapHandler();
        var node = new KnowledgeNode(new FakePipeline(KnowledgeResponse.Unanswered("gap")), NullLogger<KnowledgeNode>.Instance, handler);

        var slots = new Dictionary<string, string> { [PacketSlots.GapFillDepth] = "1", [PacketSlots.Blocking] = "true" };
        await node.HandleAsync(Routed(slots));

        Assert.False(handler.Invoked);   // leaf fill must not recurse into gap handling
    }
}
