#nullable enable

using Darci.Nodes;
using Darci.Research.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

/// <summary>Sub-unit 4: tooling proposals are DATA-ONLY, demand-driven, rate-limited, and critic-reviewed.
/// They never register a node — approval just records the human's intent to build it at compile time.</summary>
public sealed class ToolingProposalTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteProposalStore _proposals;

    public ToolingProposalTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-tooling-{Guid.NewGuid():N}.db");
        _proposals = new SqliteProposalStore($"Data Source={_dbPath}", NullLogger<SqliteProposalStore>.Instance);
        _proposals.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private sealed class FakeToolingCritic : IToolingCritic
    {
        public int CallCount;
        public Task<ToolingCritique> ReviewAsync(ToolingProposal proposal, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new ToolingCritique(new[] { "consider the coding node" }, NeedsNewCapability: true, "no existing env fits"));
        }
    }

    private ToolingProposalEmitter Emitter(FakeToolingCritic critic, ToolingProposalOptions? opt = null) =>
        new(_proposals, critic, opt ?? new ToolingProposalOptions(), NullLogger<ToolingProposalEmitter>.Instance);

    private static ToolingProposal WithDemand() =>
        new("run a physics simulation", Capability.GenerateCad, NodeId.Cad, "INode advertising GenerateCad",
            BlockedCampaignIds: new[] { "camp-1" }, BlockedStepIds: new[] { "s1" }, OpenGapIds: new[] { "gap-1" });

    private static ToolingProposal NoDemand() =>
        new("speculative tool", Capability.GenerateCad, NodeId.Cad, "sketch",
            BlockedCampaignIds: System.Array.Empty<string>(), BlockedStepIds: System.Array.Empty<string>(), OpenGapIds: System.Array.Empty<string>());

    [Fact]
    public async Task WithDemand_FilesDataOnlyProposal_CriticReviewed()
    {
        var critic = new FakeToolingCritic();
        var hp = await Emitter(critic).EmitAsync(WithDemand());

        Assert.NotNull(hp);
        Assert.Equal(HumanProposalKind.ProposeTooling, hp!.Kind);
        Assert.Equal(Capability.GenerateCad.ToString(), hp.SubjectId);
        Assert.Null(hp.ParkedPacketId);                 // DATA-ONLY: nothing is blocked ON the proposal
        Assert.Equal(1, critic.CallCount);              // critic reviewed ("what existing env could run this?")
        Assert.Contains("physics simulation", hp.JustificationJson);
        Assert.Single(await _proposals.GetPendingAsync());
    }

    [Fact]
    public async Task NoDemand_Refused()
    {
        var hp = await Emitter(new FakeToolingCritic()).EmitAsync(NoDemand());
        Assert.Null(hp);                                // demand-driven: must cite ≥1 blocker
        Assert.Empty(await _proposals.GetPendingAsync());
    }

    [Fact]
    public async Task RateLimited_DedupesPerCapability()
    {
        var emitter = Emitter(new FakeToolingCritic(), new ToolingProposalOptions { MaxOpenPerCapability = 1 });

        var first = await emitter.EmitAsync(WithDemand());
        var second = await emitter.EmitAsync(WithDemand());   // same capability already open

        Assert.NotNull(first);
        Assert.Null(second);                            // suppressed by the rate limit
        Assert.Single(await _proposals.GetPendingAsync());
    }

    [Fact]
    public async Task Approval_IsDataOnly_RecordedWithoutSideEffects()
    {
        var hp = await Emitter(new FakeToolingCritic()).EmitAsync(WithDemand());

        // No campaign coordinator needed: approving a tooling proposal just records intent — it never
        // registers a node (that stays compile-time). The gate handles it as a plain recorded decision.
        var gate = new HumanGateService(_proposals, new StubInnovated(), new StubPackets(), NullLogger<HumanGateService>.Instance);
        var result = await gate.DecideAsync(hp!.Id, approve: true, note: "will build", decidedBy: "tinman");

        Assert.True(result.Applied);
        Assert.Equal(HumanProposalStatus.Approved, (await _proposals.GetAsync(hp.Id))!.Status);
        Assert.Empty(await _proposals.GetPendingAsync());
    }

    [Fact]
    public void Critic_Parse_ExtractsAlternativesAndNeed()
    {
        var c = OllamaToolingCritic.Parse("""{"existingAlternatives": ["coding node"], "needsNewCapability": false, "summary": "reuse coding"}""");
        Assert.NotNull(c);
        Assert.Single(c!.ExistingAlternatives);
        Assert.False(c.NeedsNewCapability);
    }

    // Minimal stubs so the gate can record a decision on a data-only proposal (no promotion/campaign path hit).
    private sealed class StubInnovated : IInnovatedKnowledgeStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAsync(InnovatedKnowledgeRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(InnovatedKnowledgeRecord record, LedgerEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<InnovatedKnowledgeRecord?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<InnovatedKnowledgeRecord?>(null);
        public Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InnovatedKnowledgeRecord>>(System.Array.Empty<InnovatedKnowledgeRecord>());
        public Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetByProvenanceAsync(Provenance provenance, int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InnovatedKnowledgeRecord>>(System.Array.Empty<InnovatedKnowledgeRecord>());
        public Task<IReadOnlyList<InnovatedRevision>> GetRevisionsAsync(string entryId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InnovatedRevision>>(System.Array.Empty<InnovatedRevision>());
        public Task<bool> RevertToRevisionAsync(string entryId, int revisionSeq, CancellationToken ct = default) => Task.FromResult(false);
        public Task RecordConsumptionAsync(string entryId, string correlationRoot, double weight = 1.0, string? campaignId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetEntriesByConsumptionRootAsync(string correlationRoot, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InnovatedKnowledgeRecord>>(System.Array.Empty<InnovatedKnowledgeRecord>());
        public Task<bool> ResolveConsumptionAsync(string entryId, string correlationRoot, ConsumptionOutcome outcome, CancellationToken ct = default) => Task.FromResult(false);
        public Task<(int Successes, int Failures)> CountDistinctOutcomesAsync(string entryId, CancellationToken ct = default) => Task.FromResult((0, 0));
        public Task<IReadOnlyList<InnovatedConsumption>> GetConsumptionsAsync(string entryId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InnovatedConsumption>>(System.Array.Empty<InnovatedConsumption>());
    }

    private sealed class StubPackets : INodePacketStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CreatePacketAsync(NodePacket packet, CancellationToken ct = default) => Task.CompletedTask;
        public Task SavePacketAsync(NodePacket packet, CancellationToken ct = default) => Task.CompletedTask;
        public Task<NodePacket?> GetPacketAsync(string packetId, CancellationToken ct = default) => Task.FromResult<NodePacket?>(null);
        public Task<NodePacketStatus?> GetStatusAsync(string packetId, CancellationToken ct = default) => Task.FromResult<NodePacketStatus?>(null);
        public Task<IReadOnlyList<NodePacket>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodePacket>>(System.Array.Empty<NodePacket>());
        public Task<IReadOnlyList<NodePacket>> GetByStatesAsync(IReadOnlyList<NodeState> states, int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodePacket>>(System.Array.Empty<NodePacket>());
        public Task<IReadOnlyList<NodePacket>> GetActivePacketsWithExpiredLeaseAsync(DateTime nowUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodePacket>>(System.Array.Empty<NodePacket>());
        public Task<IReadOnlyList<NodePacket>> GetActivePacketsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodePacket>>(System.Array.Empty<NodePacket>());
    }
}
