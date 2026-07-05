#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

/// <summary>Sub-unit 3: the objective sandbox PoC gate. Attaches self-generated evidence that is
/// WEIGHT-CAPPED and never moves provenance/confidence — the node cannot lift its own entry by testing it.</summary>
public sealed class SandboxPoCGateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteInnovatedKnowledgeStore _innovated;

    public SandboxPoCGateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-poc-{Guid.NewGuid():N}.db");
        _innovated = new SqliteInnovatedKnowledgeStore($"Data Source={_dbPath}", NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        _innovated.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private sealed class FakeNode : INode
    {
        public NodeId Id { get; }
        public IReadOnlySet<Capability> Capabilities { get; }
        public FakeNode(NodeId id, params Capability[] caps) { Id = id; Capabilities = new HashSet<Capability>(caps); }
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default) => Task.FromResult(packet);
    }

    private sealed class FakeRouter : INodeRouter
    {
        private readonly bool _pass;
        public FakeRouter(bool pass) => _pass = pass;
        public Task<NodePacket> DispatchAsync(NodePacket packet, CancellationToken ct = default)
        {
            var p = packet
                .Transition(NodeId.Coding, NodeState.Routed, "r")
                .Transition(NodeId.Coding, NodeState.Accepted, "a")
                .Transition(NodeId.Coding, NodeState.Working, "w", leaseFor: TimeSpan.FromMinutes(1))
                .WithSlot(PacketSlots.StepMeasurements, JsonSerializer.Serialize(new Dictionary<string, double> { ["compiles"] = 1 }));
            p = _pass
                ? p.Transition(NodeId.Coding, NodeState.Succeeded, "built", success: true)
                : p.Transition(NodeId.Coding, NodeState.Failed, "compile error", success: false);
            return Task.FromResult(p);
        }
    }

    private SandboxPoCGate Gate(bool pass, bool hasEnv = true, SandboxPoCOptions? opt = null)
    {
        INode[] nodes = hasEnv ? new INode[] { new FakeNode(NodeId.Coding, Capability.RunTests) } : System.Array.Empty<INode>();
        return new SandboxPoCGate(new FakeRouter(pass), _innovated, nodes, opt ?? new SandboxPoCOptions(), NullLogger<SandboxPoCGate>.Instance);
    }

    private async Task<InnovatedKnowledgeRecord> SeedAsync()
    {
        var rec = new InnovatedKnowledgeRecord { Hypothesis = "combine A and B", Topic = "t", Intent = "i", Provenance = Provenance.Innovated, Confidence = Confidence.Of(0.3) };
        await _innovated.AddAsync(rec);
        return rec;
    }

    [Fact]
    public async Task Passed_AppendsSuccessEvidence_ProvenanceAndConfidenceUnchanged()
    {
        var entry = await SeedAsync();
        var poc = await Gate(pass: true).AttachAsync(entry);

        Assert.NotNull(poc);
        Assert.True(poc!.Passed);
        Assert.Equal(0.25, poc.Weight, 5);

        var after = await _innovated.GetAsync(entry.Id);
        Assert.Equal(Provenance.Innovated, after!.Provenance);      // self-testing never moves trust
        Assert.Equal(0.3, after.Confidence.Score, 5);              // confidence untouched

        var revs = await _innovated.GetRevisionsAsync(entry.Id);
        Assert.Contains(revs, r => r.Kind == LedgerEventKind.SuccessEvidence && r.CorrelationRoot == SandboxPoCGate.SandboxRoot);
    }

    [Fact]
    public async Task Failed_AppendsFailureEvidence_ProvenanceUnchanged()
    {
        var entry = await SeedAsync();
        var poc = await Gate(pass: false).AttachAsync(entry);

        Assert.False(poc!.Passed);
        Assert.Equal(Provenance.Innovated, (await _innovated.GetAsync(entry.Id))!.Provenance);
        var revs = await _innovated.GetRevisionsAsync(entry.Id);
        Assert.Contains(revs, r => r.Kind == LedgerEventKind.FailureEvidence && r.CorrelationRoot == SandboxPoCGate.SandboxRoot);
    }

    [Fact]
    public async Task WeightCap_TotalSelfGeneratedWeight_NeverExceedsCap()
    {
        var entry = await SeedAsync();
        var gate = Gate(pass: true, opt: new SandboxPoCOptions { PerRunWeight = 0.25, SandboxWeightCap = 0.5 });

        // Test itself all day — the cumulative self-generated weight stays bounded at the cap.
        for (var i = 0; i < 6; i++) await gate.AttachAsync(entry);

        var total = await gate.AccumulatedSandboxWeightAsync(entry.Id);
        Assert.True(total <= 0.5 + 1e-9, $"cumulative sandbox weight {total} exceeded the cap");
        Assert.Equal(0.5, total, 5);   // reached but did not exceed the ceiling
    }

    [Fact]
    public async Task NoSandboxEnvironment_ReturnsNull_NoLedgerChange()
    {
        var entry = await SeedAsync();
        var poc = await Gate(pass: true, hasEnv: false).AttachAsync(entry);

        Assert.Null(poc);
        var revs = await _innovated.GetRevisionsAsync(entry.Id);
        Assert.DoesNotContain(revs, r => r.CorrelationRoot == SandboxPoCGate.SandboxRoot);
    }
}
