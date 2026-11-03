#nullable enable

using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

/// <summary>
/// Phase D governor: bounded diverse-candidate loop. Covers the required behaviours — 3 diverse candidates
/// generated + screened, only survivors falsified, plateau stop, novelty-collapse → Unsolvable, budget
/// backstop halts, adaptive N drops under pressure, and the archive seeds the next cycle while retracted
/// ideas are excluded.
/// </summary>
public sealed class InnovationLoopGovernorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteInnovatedKnowledgeStore _store;

    public InnovationLoopGovernorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-innovloop-{Guid.NewGuid():N}.db");
        _store = new SqliteInnovatedKnowledgeStore($"Data Source={_dbPath}", NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // ── controllable fakes ──

    /// <summary>Cycles through a fixed candidate list by call-index; records the avoid/failed lists each call.</summary>
    private sealed class RecordingGenerator : IInnovationSynthesizer
    {
        private readonly IReadOnlyList<InnovationProposal> _cands;
        public int CallCount;
        public readonly List<(List<string> Avoid, List<string> Failed)> Calls = new();
        public RecordingGenerator(params InnovationProposal[] cands) => _cands = cands;

        public Task<InnovationProposal> SynthesizeAsync(InnovationRequest request, CancellationToken ct = default)
            => GenerateCandidateAsync(request, Array.Empty<string>(), Array.Empty<string>(), ct);

        public Task<InnovationProposal> GenerateCandidateAsync(
            InnovationRequest request, IReadOnlyList<string> avoid, IReadOnlyList<string> failed, CancellationToken ct = default)
        {
            Calls.Add((avoid.ToList(), failed.ToList()));
            var c = _cands[CallCount % _cands.Count];
            CallCount++;
            return Task.FromResult(c);
        }
    }

    private sealed class FakeCritic : IInnovationCritic
    {
        private readonly int[] _ranking;
        public int CallCount;
        public int LastCandidateCount;
        public FakeCritic(params int[] ranking) => _ranking = ranking;

        public Task<IReadOnlyList<int>> ScreenAsync(InnovationRequest request, IReadOnlyList<InnovationProposal> candidates, CancellationToken ct = default)
        {
            CallCount++;
            LastCandidateCount = candidates.Count;
            var r = _ranking.Where(i => i >= 0 && i < candidates.Count).ToList();
            foreach (var i in Enumerable.Range(0, candidates.Count)) if (!r.Contains(i)) r.Add(i);
            return Task.FromResult<IReadOnlyList<int>>(r);
        }
    }

    /// <summary>Returns the embedding registered for a candidate's hypothesis (empty map entry → the given default).</summary>
    private sealed class EmbeddingToolbox : IResearchToolbox
    {
        private readonly Dictionary<string, float[]> _map;
        public EmbeddingToolbox(Dictionary<string, float[]> map) => _map = map;
        public Task<string> GenerateAsync(string prompt, CancellationToken ct = default) => Task.FromResult("");
        public Task<List<float>> GetEmbeddingAsync(string text, CancellationToken ct = default)
            => Task.FromResult((_map.TryGetValue(text, out var v) ? v : new[] { 0.01f, 0.01f }).ToList());
        public Task<string> SearchWebAsync(string query, CancellationToken ct = default) => Task.FromResult("");
    }

    private static InnovationProposal Cand(string hypothesis) => new()
    {
        Status = ProposalStatus.Proposed,
        Hypothesis = hypothesis,
        Provenance = Provenance.Innovated,
        Confidence = ProvenancePolicy.Clamp(Provenance.Innovated, Confidence.Of(0.3)),
    };

    private static InnovationRequest Request() =>
        new("how to close the grip loop?", "build a grip controller", null, new List<string>(), new List<string> { "fact-a" });

    private InnovationLoopGovernor Governor(
        IInnovationSynthesizer gen, IInnovationCritic critic, IKnowledgeReviewAgent reviewer,
        IResearchToolbox toolbox, InnovationLoopOptions opt) =>
        new(gen, critic, reviewer, toolbox, _store, opt, NullLogger<InnovationLoopGovernor>.Instance);

    // ── tests ──

    [Fact]
    public async Task ThreeDiverseCandidates_Generated_ScreenedOnce_OnlySurvivorsFalsified()
    {
        var gen = new RecordingGenerator(Cand("cand-A"), Cand("cand-B"), Cand("cand-C"));
        var critic = new FakeCritic(2, 0, 1);            // C best, then A, then B
        var reviewer = new FakeReviewAgent(FakeReviewAgent.Accept(0.8), FakeReviewAgent.Accept(0.6));
        var toolbox = new EmbeddingToolbox(new()
        {
            ["cand-A"] = new[] { 1f, 0f, 0f },
            ["cand-B"] = new[] { 0f, 1f, 0f },
            ["cand-C"] = new[] { 0f, 0f, 1f },
        });
        var opt = new InnovationLoopOptions { CandidatesPerCycle = 3, SurvivorsToFalsify = 2, AcceptThreshold = 0.5 };

        var result = await Governor(gen, critic, reviewer, toolbox, opt).RunAsync(Request());

        Assert.Equal(3, gen.CallCount);                 // three diverse candidates generated (N=3)
        Assert.Equal(1, critic.CallCount);              // ONE comparative screen call
        Assert.Equal(3, critic.LastCandidateCount);     // over all three
        Assert.Equal(2, reviewer.CallCount);            // only the top-2 survivors fully falsified
        Assert.Equal(ProposalStatus.VettedInternally, result.Status);
        Assert.Equal("cand-C", result.Hypothesis);      // highest-quality survivor won
        Assert.True(result.Confidence.IsLow);           // stored capped — never crosses the IsLow line
        Assert.NotNull(result.Plausibility);

        // Each generation after the first was told to differ from the earlier candidates this cycle.
        Assert.Empty(gen.Calls[0].Avoid);
        Assert.Contains("cand-A", gen.Calls[1].Avoid);
        Assert.Contains("cand-B", gen.Calls[2].Avoid);
    }

    [Fact]
    public async Task NoveltyCollapse_WithinCycle_ReturnsUnsolvable_WithoutScreeningOrFalsifying()
    {
        // Three "diverse-prompted" candidates come back near-identical → substrate exhausted this cycle.
        var gen = new RecordingGenerator(Cand("near-1"), Cand("near-2"), Cand("near-3"));
        var critic = new FakeCritic(0, 1, 2);
        var reviewer = new FakeReviewAgent();
        var toolbox = new EmbeddingToolbox(new()
        {
            ["near-1"] = new[] { 1f, 0f, 0f },
            ["near-2"] = new[] { 1f, 0f, 0f },
            ["near-3"] = new[] { 1f, 0f, 0f },
        });
        var opt = new InnovationLoopOptions { CandidatesPerCycle = 3, NoveltyCollapseThreshold = 0.12 };

        var result = await Governor(gen, critic, reviewer, toolbox, opt).RunAsync(Request());

        Assert.Equal(ProposalStatus.Unsolvable, result.Status);
        Assert.Contains(result.RequiredExternalInputs, s => s.Contains("external data"));
        Assert.Equal(0, critic.CallCount);              // collapsed before screening
        Assert.Equal(0, reviewer.CallCount);            // and before any falsification
    }

    [Fact]
    public async Task Plateau_StopsAfterK_NoImprovementCycles()
    {
        // Same two candidates + same flat quality every cycle → no progress after the first.
        var gen = new RecordingGenerator(Cand("flat-A"), Cand("flat-B"));
        var critic = new FakeCritic(0, 1);              // A always screens first
        var reviewer = new FakeReviewAgent();           // empty queue → constant 0.7 verdict
        var toolbox = new EmbeddingToolbox(new()
        {
            ["flat-A"] = new[] { 1f, 0f },
            ["flat-B"] = new[] { 0f, 1f },
        });
        var opt = new InnovationLoopOptions
        {
            CandidatesPerCycle = 2, MinCandidates = 2, SurvivorsToFalsify = 1,
            AcceptThreshold = 0.9, PlateauCycles = 2, Budget = new InnovationBudget(MaxCycles: 10),
        };

        var result = await Governor(gen, critic, reviewer, toolbox, opt).RunAsync(Request());

        // cycle1 improves (0→0.7); cycles 2 and 3 flat → plateau hits after 2 no-progress cycles → 3 cycles × N=2.
        Assert.Equal(6, gen.CallCount);
        Assert.NotEqual(ProposalStatus.Unsolvable, result.Status);   // a winner was still returned
    }

    [Fact]
    public async Task BudgetBackstop_HaltsLoop_RegardlessOfProgress()
    {
        // Never accepts, never collapses, always "improves" — only the hard call budget can stop it.
        var gen = new RecordingGenerator(Cand("b-A"), Cand("b-B"), Cand("b-C"), Cand("b-D"));
        var critic = new FakeCritic(0, 1, 2);
        var reviewer = new FakeReviewAgent(
            FakeReviewAgent.Accept(0.30), FakeReviewAgent.Accept(0.50),
            FakeReviewAgent.Accept(0.70), FakeReviewAgent.Accept(0.85));
        var toolbox = new EmbeddingToolbox(new()
        {
            ["b-A"] = new[] { 1f, 0f, 0f, 0f },
            ["b-B"] = new[] { 0f, 1f, 0f, 0f },
            ["b-C"] = new[] { 0f, 0f, 1f, 0f },
            ["b-D"] = new[] { 0f, 0f, 0f, 1f },
        });
        var opt = new InnovationLoopOptions
        {
            CandidatesPerCycle = 3, AcceptThreshold = 0.99,
            Budget = new InnovationBudget(MaxCycles: 100, MaxGenerativeCalls: 4, MaxWallClockSeconds: 180),
        };

        var result = await Governor(gen, critic, reviewer, toolbox, opt).RunAsync(Request());

        Assert.True(gen.CallCount <= 4, $"budget should bound generation, got {gen.CallCount}");
        Assert.NotEqual(ProposalStatus.Unsolvable, result.Status);   // returns the best found so far
    }

    [Fact]
    public void AdaptiveN_DropsFromThreeToTwo_UnderBudgetPressure()
    {
        var budget = new InnovationBudget(MaxGenerativeCalls: 24);
        var opt = new InnovationLoopOptions { CandidatesPerCycle = 3, MinCandidates = 2, SurvivorsToFalsify = 2, BudgetPressureFraction = 0.6 };

        Assert.Equal(3, InnovationLoopGovernor.AdaptiveN(calls: 0, budget, opt));    // fresh budget → full N
        Assert.Equal(2, InnovationLoopGovernor.AdaptiveN(calls: 15, budget, opt));   // ≥60% spent → drop to floor
    }

    [Fact]
    public async Task Archive_SeedsNextCycle_AndRetractedIdeasAreExcluded()
    {
        // A previously-retracted idea lives in the store; it must be fed to the generator as a negative.
        await _store.AddAsync(new InnovatedKnowledgeRecord
        {
            CorrelationId = "old", Hypothesis = "old failed idea", Topic = "t", Intent = "i",
            Provenance = Provenance.Retracted, Confidence = Confidence.Of(0.0),
        });

        var gen = new RecordingGenerator(Cand("seed-A"), Cand("seed-B"));
        var critic = new FakeCritic(0, 1);
        var reviewer = new FakeReviewAgent();
        var toolbox = new EmbeddingToolbox(new()
        {
            ["seed-A"] = new[] { 1f, 0f },
            ["seed-B"] = new[] { 0f, 1f },
        });
        var opt = new InnovationLoopOptions
        {
            CandidatesPerCycle = 2, MinCandidates = 2, SurvivorsToFalsify = 1,
            AcceptThreshold = 0.99, PlateauCycles = 5, Budget = new InnovationBudget(MaxCycles: 2),
        };

        await Governor(gen, critic, reviewer, toolbox, opt).RunAsync(Request());

        // Retracted negative reached every generation call.
        Assert.All(gen.Calls, c => Assert.Contains("old failed idea", c.Failed));

        // Archive seeds the next cycle: the first call of cycle 2 avoids cycle-1's archived hypotheses.
        Assert.True(gen.CallCount >= 3, "expected at least two cycles");
        var firstOfCycle2 = gen.Calls[2];
        Assert.Contains(firstOfCycle2.Avoid, h => h is "seed-A" or "seed-B");
    }
}
