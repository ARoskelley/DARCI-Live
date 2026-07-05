#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

public sealed class InnovationNodeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteInnovatedKnowledgeStore _store;

    public InnovationNodeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-innovnode-{Guid.NewGuid():N}.db");
        _store = new SqliteInnovatedKnowledgeStore($"Data Source={_dbPath}", NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private sealed class FakeSynthesizer : IInnovationSynthesizer
    {
        private readonly InnovationProposal _p;
        public InnovationRequest? Last;
        public FakeSynthesizer(InnovationProposal p) => _p = p;
        public Task<InnovationProposal> SynthesizeAsync(InnovationRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.FromResult(_p);
        }
    }

    private InnovationNode Node(IInnovationSynthesizer synth, FakeReviewAgent reviewer) =>
        new(synth, reviewer, _store, NullLogger<InnovationNode>.Instance);

    private static NodePacket Routed(IReadOnlyDictionary<string, string>? slots = null) =>
        NodePacket.Create("design a myoelectric grip controller", capability: Capability.Innovate, slots: slots)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");

    private static InnovationProposal SolvableCandidate() => new()
    {
        Status = ProposalStatus.Proposed,
        Hypothesis = "combine EMG threshold detection with a PID grip loop",
        Reasoning = new[] { new ReasoningLink("EMG amplitude maps to intent", new[] { "f1" }) },
        Provenance = Provenance.Innovated,
        Confidence = ProvenancePolicy.Clamp(Provenance.Innovated, Confidence.Of(0.3)),
    };

    [Fact]
    public async Task SinglePass_Solvable_Reviews_Persists_AndReturnsProposal()
    {
        var synth = new FakeSynthesizer(SolvableCandidate());
        var reviewer = new FakeReviewAgent(FakeReviewAgent.Accept(0.9)); // separate evaluator; high but must not lift trust
        var node = Node(synth, reviewer);

        var slots = new Dictionary<string, string>
        {
            [PacketSlots.Question] = "how to close the grip loop?",
            [PacketSlots.InnovationKnownFacts] = JsonSerializer.Serialize(new[] { "EMG sensors give amplitude", "PID controls actuators" }),
        };
        var result = await node.HandleAsync(Routed(slots));

        Assert.Equal(NodeState.Succeeded, result.State);

        // Generator ≠ evaluator: the reviewer ran separately.
        Assert.Equal(1, reviewer.CallCount);

        // Structured proposal in the slot, vetted, capped.
        var proposal = JsonSerializer.Deserialize<InnovationProposal>(result.Payload.Slot(PacketSlots.InnovationProposal)!);
        Assert.Equal(ProposalStatus.VettedInternally, proposal!.Status);
        Assert.NotNull(proposal.Plausibility);
        Assert.True(proposal.Confidence.IsLow);

        // Persisted as an Innovated hypothesis — capped and NOT promoted (invariant §0a).
        var persisted = await _store.GetByProvenanceAsync(Provenance.Innovated);
        Assert.Single(persisted);
        Assert.Equal("combine EMG threshold detection with a PID grip loop", persisted[0].Hypothesis);
        Assert.True(persisted[0].Confidence.Score <= ProvenancePolicy.InnovatedCap);
        Assert.Equal(Provenance.Innovated, persisted[0].Provenance);   // never higher without a human event
    }

    [Fact]
    public async Task HighReviewerConfidence_DoesNotLiftAboveCap()
    {
        var synth = new FakeSynthesizer(SolvableCandidate());
        var reviewer = new FakeReviewAgent(FakeReviewAgent.Accept(0.99));
        var result = await Node(synth, reviewer).HandleAsync(Routed());

        var proposal = JsonSerializer.Deserialize<InnovationProposal>(result.Payload.Slot(PacketSlots.InnovationProposal)!);
        Assert.True(proposal!.Confidence.Score <= ProvenancePolicy.InnovatedCap);
        Assert.True(proposal.Confidence.IsLow);
    }

    [Fact]
    public async Task Unsolvable_ReturnsRequiredInputs_AndDoesNotPersistOrReview()
    {
        var unsolvable = InnovationProposal.CannotSolve("no known combination works",
            new[] { "measured actuator torque curve" });
        var synth = new FakeSynthesizer(unsolvable);
        var reviewer = new FakeReviewAgent();
        var result = await Node(synth, reviewer).HandleAsync(Routed());

        Assert.Equal(NodeState.Succeeded, result.State);
        Assert.Equal(0, reviewer.CallCount);                           // nothing to review

        var proposal = JsonSerializer.Deserialize<InnovationProposal>(result.Payload.Slot(PacketSlots.InnovationProposal)!);
        Assert.Equal(ProposalStatus.Unsolvable, proposal!.Status);
        Assert.Contains("measured actuator torque curve", proposal.RequiredExternalInputs);

        Assert.Empty(await _store.GetByProvenanceAsync(Provenance.Innovated));   // no hypothesis persisted
    }

    [Fact]
    public async Task RequestIsBuiltFromSlots()
    {
        var synth = new FakeSynthesizer(SolvableCandidate());
        var node = Node(synth, new FakeReviewAgent(FakeReviewAgent.Accept()));
        var slots = new Dictionary<string, string>
        {
            [PacketSlots.Question] = "Q",
            [PacketSlots.FailureContext] = "research returned nothing",
            [PacketSlots.InnovationGaps] = JsonSerializer.Serialize(new[] { "g1" }),
            [PacketSlots.InnovationKnownFacts] = JsonSerializer.Serialize(new[] { "fact-a", "fact-b" }),
        };
        await node.HandleAsync(Routed(slots));

        Assert.Equal("Q", synth.Last!.Question);
        Assert.Equal("research returned nothing", synth.Last.FailureContext);
        Assert.Equal(new[] { "g1" }, synth.Last.GapList);
        Assert.Equal(new[] { "fact-a", "fact-b" }, synth.Last.FactList);
    }
}
