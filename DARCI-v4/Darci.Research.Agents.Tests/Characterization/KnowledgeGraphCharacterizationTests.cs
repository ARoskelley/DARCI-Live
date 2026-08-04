#nullable enable

using Darci.Memory.Confidence;
using Darci.Memory.Confidence.Models;
using Darci.Memory.Graph;
using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests.Characterization;

/// <summary>
/// P2c.2 — THE ORACLE EXTENSION, and it is load-bearing.
///
/// <para>The SU0 characterization harness fakes <c>IInnovationLoop</c> and <c>IKnowledgePipeline</c>, so it
/// never executes a single real knowledge-graph call. Relying on SU0 alone as the no-behavior-change proof
/// for P2c.3 would mean it passes while proving nothing about the refactor — the classic green-but-blind
/// trap. These tests run the REAL <see cref="OllamaInnovationSynthesizer"/> and
/// <see cref="KnowledgeAssessor"/> against a REAL SQLite <see cref="KnowledgeGraph"/>, faking only the LLM.
/// </para>
///
/// <para>They must pass UNCHANGED after P2c.3 moves those call sites onto the memory broker.</para>
/// </summary>
public sealed class KnowledgeGraphCharacterizationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly KnowledgeGraph _graph;

    public KnowledgeGraphCharacterizationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-kgchar-{Guid.NewGuid():N}.db");
        var conn = $"Data Source={_dbPath}";
        _graph = new KnowledgeGraph(conn, NullLogger<KnowledgeGraph>.Instance);
        _graph.InitializeAsync().GetAwaiter().GetResult();

        // The graph's entity upsert refreshes entity confidence from confidence_claims, so both schemas must
        // exist — exactly as in production, where they share darci.db.
        new ConfidenceTracker(conn, _graph, NullLogger<ConfidenceTracker>.Instance)
            .InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    /// <summary>The real graph, reached the way nodes now reach it — through the broker. The underlying
    /// store and data are identical, which is what makes these assertions a genuine before/after proof.</summary>
    private IMemoryBroker Memory => new MemoryBroker(_graph, NullLogger<MemoryBroker>.Instance);

    private async Task SeedGraphAsync()
    {
        await _graph.UpsertEntityAsync("EMG sensor", "Component", "biomed",
            "Measures muscle electrical activity.");
        await _graph.UpsertEntityAsync("PID controller", "Technique", "control",
            "Closed-loop controller using proportional, integral and derivative terms.");
        await _graph.UpsertEntityAsync("Unrelated widget", "Component", "misc", "Nothing to do with grip.");
    }

    /// <summary>Captures the prompt the synthesizer built, so we can assert the graph context reached it.</summary>
    private sealed class PromptCapturingToolbox : IResearchToolbox
    {
        public string? LastPrompt;
        private readonly string _response;
        public PromptCapturingToolbox(string response) => _response = response;

        public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(_response);
        }
        public Task<List<float>> GetEmbeddingAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new List<float> { 0.1f, 0.2f, 0.3f });
        public Task<string> SearchWebAsync(string query, CancellationToken ct = default) => Task.FromResult("");
    }

    private const string SolvableJson = """
        {"solvable": true, "hypothesis": "combine EMG thresholding with a PID grip loop",
         "reasoning": [{"inference": "EMG amplitude maps to intent", "citedFacts": ["EMG sensor"]}],
         "assumptions": [], "requiredExternalInputs": []}
        """;

    // ── InnovationSynthesizer: the KG call site on the innovation path ──

    [Fact]
    public async Task Synthesizer_PullsRelatedConceptsFromTheRealGraph_IntoItsPrompt()
    {
        await SeedGraphAsync();
        var toolbox = new PromptCapturingToolbox(SolvableJson);
        var synthesizer = new OllamaInnovationSynthesizer(toolbox, Memory, NullLogger<OllamaInnovationSynthesizer>.Instance);

        // The question must be a SUBSTRING of an entity name for the current graph search to match — see
        // Synthesizer_WithANaturalLanguageQuestion_FindsNothing below for why that matters.
        var proposal = await synthesizer.SynthesizeAsync(
            new InnovationRequest("EMG sensor", "build a grip controller"));

        // The graph context block, and the matched entity with its description, reached the model.
        Assert.NotNull(toolbox.LastPrompt);
        Assert.Contains("Related concepts in the knowledge graph:", toolbox.LastPrompt);
        Assert.Contains("EMG sensor", toolbox.LastPrompt);
        Assert.Contains("Measures muscle electrical activity.", toolbox.LastPrompt);

        // And the proposal still comes back capped-Innovated, as always.
        Assert.Equal(ProposalStatus.Proposed, proposal.Status);
        Assert.Equal(Provenance.Innovated, proposal.Provenance);
        Assert.True(proposal.Confidence.IsLow);
    }

    [Fact]
    public async Task Synthesizer_WithANaturalLanguageQuestion_FindsNothing()
    {
        // CHARACTERIZING A REAL PRE-EXISTING QUIRK, not endorsing it. GatherGraphContextAsync passes the
        // WHOLE question to SearchEntitiesAsync, which matches `name LIKE '%<query>%'`. A natural-language
        // question is therefore almost never a substring of an entity name, so the innovation node's
        // "related concepts from the KG" grounding is effectively inert in production — which is consistent
        // with the live Run A/B observations, where innovation reached Unsolvable with no graph grounding.
        //
        // This test pins TODAY'S behavior so the P2c.3 broker move is proven not to change it. Fixing the
        // search (keyword extraction / semantic search) is a separate, deliberate change.
        await SeedGraphAsync();
        var toolbox = new PromptCapturingToolbox(SolvableJson);
        var synthesizer = new OllamaInnovationSynthesizer(toolbox, Memory, NullLogger<OllamaInnovationSynthesizer>.Instance);

        await synthesizer.SynthesizeAsync(
            new InnovationRequest("How do I close an EMG grip loop?", "build a grip controller"));

        Assert.DoesNotContain("Related concepts in the knowledge graph:", toolbox.LastPrompt);
    }

    [Fact]
    public async Task Synthesizer_WithAnEmptyGraph_OmitsTheContextBlockEntirely()
    {
        // No seeding: the graph is empty. The context section must be absent, not an empty header.
        var toolbox = new PromptCapturingToolbox(SolvableJson);
        var synthesizer = new OllamaInnovationSynthesizer(toolbox, Memory, NullLogger<OllamaInnovationSynthesizer>.Instance);

        await synthesizer.SynthesizeAsync(new InnovationRequest("anything at all", "intent"));

        Assert.DoesNotContain("Related concepts in the knowledge graph:", toolbox.LastPrompt);
    }

    [Fact]
    public async Task Synthesizer_SurvivesAGraphFailure_AndStillProduces()
    {
        // Graph lookup is best-effort: a broken graph degrades the prompt, it does not fail the synthesis.
        var toolbox = new PromptCapturingToolbox(SolvableJson);
        var synthesizer = new OllamaInnovationSynthesizer(
            toolbox,
            new MemoryBroker(new ThrowingGraph(), NullLogger<MemoryBroker>.Instance),
            NullLogger<OllamaInnovationSynthesizer>.Instance);

        var proposal = await synthesizer.SynthesizeAsync(new InnovationRequest("q", "i"));

        Assert.Equal(ProposalStatus.Proposed, proposal.Status);
        Assert.DoesNotContain("Related concepts", toolbox.LastPrompt);
    }

    // ── KnowledgeAssessor: the KG call site on the knowledge path ──

    [Fact]
    public async Task Assessor_ReturnsRelevantEntitiesFromTheRealGraph()
    {
        await SeedGraphAsync();
        var assessor = new KnowledgeAssessor(
            Memory, new StubConfidenceTracker(0.1), new PromptCapturingToolbox("{}"),
            NullLogger<KnowledgeAssessor>.Instance);

        var assessment = await assessor.AssessAsync("EMG sensor");

        Assert.NotEmpty(assessment.RelevantEntities);
        Assert.Contains(assessment.RelevantEntities, e => e.Name == "EMG sensor");
        Assert.Equal("EMG sensor", assessment.Topic);
    }

    [Fact]
    public async Task Assessor_LowGraphConfidence_RunsAgents()
    {
        await SeedGraphAsync();
        var assessor = new KnowledgeAssessor(
            Memory, new StubConfidenceTracker(0.05), new PromptCapturingToolbox("{}"),
            NullLogger<KnowledgeAssessor>.Instance);

        var assessment = await assessor.AssessAsync("EMG sensor");

        Assert.Equal(DispatchDecision.RunAgents, assessment.Decision);
        Assert.True(assessment.Confidence.IsLow);
    }

    [Fact]
    public async Task Assessor_HighGraphConfidence_SkipsAgents()
    {
        await SeedGraphAsync();
        var assessor = new KnowledgeAssessor(
            Memory, new StubConfidenceTracker(0.95), new PromptCapturingToolbox("{}"),
            NullLogger<KnowledgeAssessor>.Instance);

        var assessment = await assessor.AssessAsync("EMG sensor");

        Assert.Equal(DispatchDecision.SkipAgents, assessment.Decision);
        Assert.False(assessment.Confidence.IsLow);
        Assert.NotEmpty(assessment.RelevantEntities);
    }

    // ── the graph itself: the read shapes the broker must preserve ──

    [Fact]
    public async Task SearchEntities_MatchesByNameAndRespectsLimit()
    {
        await SeedGraphAsync();

        var hits = await _graph.SearchEntitiesAsync("EMG", limit: 6);
        Assert.Contains(hits, e => e.Name == "EMG sensor");

        var capped = await _graph.SearchEntitiesAsync("e", limit: 1);
        Assert.True(capped.Count <= 1);
    }

    [Fact]
    public async Task UpsertEntity_IsIdempotentByName()
    {
        var first = await _graph.UpsertEntityAsync("EMG sensor", "Component", "biomed", "v1");
        var second = await _graph.UpsertEntityAsync("EMG sensor", "Component", "biomed", "v2");

        Assert.Equal(first.Id, second.Id);   // same entity, updated — not a duplicate
        var found = await _graph.FindEntityByNameAsync("EMG sensor");
        Assert.NotNull(found);
        Assert.Equal(first.Id, found!.Id);
    }

    // ── stubs ──

    /// <summary>Only <see cref="SynthesizeAsync"/> matters here — it is what drives the assessor's decision
    /// thresholds. The rest satisfies the interface.</summary>
    private sealed class StubConfidenceTracker : IConfidenceTracker
    {
        private readonly float _aggregate;
        public StubConfidenceTracker(double aggregate) => _aggregate = (float)aggregate;

        public Task<SynthesisResult> SynthesizeAsync(string question, string? domain = null,
            Func<string, Task<List<float>>>? getEmbedding = null, CancellationToken ct = default)
            => Task.FromResult(new SynthesisResult
            {
                Question = question,
                AggregateConf = _aggregate,
                SupportingClaims = new List<KnowledgeClaim>(),
            });

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<KnowledgeClaim> AddClaimAsync(string statement, string domain, string sourceType,
            string? sourceRef = null, float sourceQuality = 0.5f, string[]? entityIds = null,
            string[]? relationIds = null, CancellationToken ct = default)
            => Task.FromResult(new KnowledgeClaim { Statement = statement });
        public Task<KnowledgeClaim?> GetClaimAsync(string id, CancellationToken ct = default)
            => Task.FromResult<KnowledgeClaim?>(null);
        public Task<IReadOnlyList<KnowledgeClaim>> GetClaimsForEntityAsync(string entityId, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KnowledgeClaim>>(Array.Empty<KnowledgeClaim>());
        public Task<IReadOnlyList<KnowledgeClaim>> GetUncertainClaimsAsync(float threshold = 0.4f, string? domain = null,
            int limit = 30, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KnowledgeClaim>>(Array.Empty<KnowledgeClaim>());
        public Task CorroborateAsync(string claimId, string sourceType, string? sourceRef = null,
            float sourcequality = 0.5f, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Contradiction> RecordContradictionAsync(string claimAId, string claimBId, float severity, CancellationToken ct = default)
            => Task.FromResult(new Contradiction());
        public Task<IReadOnlyList<Contradiction>> GetUnresolvedContradictionsAsync(string? domain = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Contradiction>>(Array.Empty<Contradiction>());
        public Task ResolveContradictionAsync(string contradictionId, string resolution, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task DecayAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingGraph : IKnowledgeGraph
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<Darci.Memory.Graph.Models.KgEntity> UpsertEntityAsync(string name, string entityType, string domain,
            string? description = null, string[]? aliases = null, float[]? embedding = null, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<Darci.Memory.Graph.Models.KgEntity?> GetEntityAsync(string id, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<Darci.Memory.Graph.Models.KgEntity?> FindEntityByNameAsync(string name, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<IReadOnlyList<Darci.Memory.Graph.Models.KgEntity>> SearchEntitiesAsync(string query, string? domain = null,
            int limit = 20, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<IReadOnlyList<Darci.Memory.Graph.Models.KgEntity>> GetEntitiesByDomainAsync(string domain,
            int limit = 100, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<Darci.Memory.Graph.Models.KgRelation> UpsertRelationAsync(string fromEntityId, string toEntityId,
            string relationType, float weight = 1, float confidence = 0.5f, string[]? evidenceIds = null, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<IReadOnlyList<Darci.Memory.Graph.Models.KgRelation>> GetRelationsAsync(string entityId,
            string? relationType = null, bool incoming = false, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<Darci.Memory.Graph.Models.GraphNeighbours> GetNeighboursAsync(string entityId, int depth = 1,
            string? relationTypeFilter = null, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<Darci.Memory.Graph.Models.GraphPath> FindPathAsync(string fromEntityId, string toEntityId,
            int maxHops = 5, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task<IReadOnlyList<(Darci.Memory.Graph.Models.KgEntity Entity, float Score)>> SemanticSearchAsync(
            float[] queryEmbedding, int limit = 10, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
        public Task IngestMemoryAsync(string memoryContent, string[] tags, Func<string, Task<List<float>>> getEmbedding,
            Func<string, Task<string>> llmExtract, CancellationToken ct = default)
            => throw new InvalidOperationException("graph down");
    }
}
