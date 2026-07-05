#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

/// <summary>The node is now thin: it runs the loop, then (for a winner) persists a capped Innovated entry
/// and files a promotion proposal. Loop internals (diversity/screen/falsify) are covered by the governor tests.</summary>
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

    private sealed class FakeLoop : IInnovationLoop
    {
        private readonly InnovationProposal _p;
        public InnovationRequest? Last;
        public int CallCount;
        public FakeLoop(InnovationProposal p) => _p = p;
        public Task<InnovationProposal> RunAsync(InnovationRequest request, CancellationToken ct = default)
        {
            Last = request;
            CallCount++;
            return Task.FromResult(_p);
        }
    }

    private InnovationNode Node(IInnovationLoop loop) =>
        new(loop, _store, NullLogger<InnovationNode>.Instance);

    private static NodePacket Routed(IReadOnlyDictionary<string, string>? slots = null) =>
        NodePacket.Create("design a myoelectric grip controller", capability: Capability.Innovate, slots: slots)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");

    // What the loop hands back for a winner: vetted, capped Innovated, plausibility attached.
    private static InnovationProposal Winner() => new()
    {
        Status = ProposalStatus.VettedInternally,
        Hypothesis = "combine EMG threshold detection with a PID grip loop",
        Reasoning = new[] { new ReasoningLink("EMG amplitude maps to intent", new[] { "f1" }) },
        Provenance = Provenance.Innovated,
        Plausibility = new KnowledgeReview(true, Confidence.Of(0.8), Array.Empty<string>(), "plausible"),
        Confidence = ProvenancePolicy.Clamp(Provenance.Innovated, Confidence.Of(0.32)),
    };

    [Fact]
    public async Task Winner_Persists_FilesProposal_AndReturnsCappedProposal()
    {
        var loop = new FakeLoop(Winner());
        var slots = new Dictionary<string, string>
        {
            [PacketSlots.Question] = "how to close the grip loop?",
            [PacketSlots.InnovationKnownFacts] = JsonSerializer.Serialize(new[] { "EMG sensors give amplitude", "PID controls actuators" }),
        };
        var result = await Node(loop).HandleAsync(Routed(slots));

        Assert.Equal(NodeState.Succeeded, result.State);
        Assert.Equal(1, loop.CallCount);

        // Structured proposal in the slot, vetted, capped (never IsLow-crossing).
        var proposal = JsonSerializer.Deserialize<InnovationProposal>(result.Payload.Slot(PacketSlots.InnovationProposal)!);
        Assert.Equal(ProposalStatus.VettedInternally, proposal!.Status);
        Assert.NotNull(proposal.Plausibility);
        Assert.True(proposal.Confidence.IsLow);

        // Persisted as an Innovated hypothesis — capped and NOT promoted (invariant §0a).
        var persisted = await _store.GetByProvenanceAsync(Provenance.Innovated);
        Assert.Single(persisted);
        Assert.Equal("combine EMG threshold detection with a PID grip loop", persisted[0].Hypothesis);
        Assert.True(persisted[0].Confidence.Score <= ProvenancePolicy.InnovatedCap);
        Assert.Equal(Provenance.Innovated, persisted[0].Provenance);
    }

    [Fact]
    public async Task Unsolvable_ReturnsRequiredInputs_AndDoesNotPersist()
    {
        var unsolvable = InnovationProposal.CannotSolve("no known combination works",
            new[] { "measured actuator torque curve" });
        var result = await Node(new FakeLoop(unsolvable)).HandleAsync(Routed());

        Assert.Equal(NodeState.Succeeded, result.State);

        var proposal = JsonSerializer.Deserialize<InnovationProposal>(result.Payload.Slot(PacketSlots.InnovationProposal)!);
        Assert.Equal(ProposalStatus.Unsolvable, proposal!.Status);
        Assert.Contains("measured actuator torque curve", proposal.RequiredExternalInputs);

        Assert.Empty(await _store.GetByProvenanceAsync(Provenance.Innovated));   // no hypothesis persisted
    }

    [Fact]
    public async Task RequestIsBuiltFromSlots()
    {
        var loop = new FakeLoop(Winner());
        var slots = new Dictionary<string, string>
        {
            [PacketSlots.Question] = "Q",
            [PacketSlots.FailureContext] = "research returned nothing",
            [PacketSlots.InnovationGaps] = JsonSerializer.Serialize(new[] { "g1" }),
            [PacketSlots.InnovationKnownFacts] = JsonSerializer.Serialize(new[] { "fact-a", "fact-b" }),
        };
        await Node(loop).HandleAsync(Routed(slots));

        Assert.Equal("Q", loop.Last!.Question);
        Assert.Equal("research returned nothing", loop.Last.FailureContext);
        Assert.Equal(new[] { "g1" }, loop.Last.GapList);
        Assert.Equal(new[] { "fact-a", "fact-b" }, loop.Last.FactList);
    }
}
