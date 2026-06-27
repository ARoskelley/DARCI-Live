#nullable enable

using Darci.Nodes;
using Darci.Memory.Confidence.Models;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;

namespace Darci.Research.Agents.Tests;

internal sealed class FakeAssessor : IKnowledgeAssessor
{
    private readonly KnowledgeAssessment _assessment;
    public FakeAssessor(KnowledgeAssessment assessment) => _assessment = assessment;
    public Task<KnowledgeAssessment> AssessAsync(string topic, CancellationToken ct = default)
        => Task.FromResult(_assessment);

    public static KnowledgeAssessment WithClaims(double confidence, params string[] statements) => new()
    {
        Confidence = Confidence.Of(confidence),
        SupportingClaims = statements.Select(s => new KnowledgeClaim { Statement = s, Confidence = (float)confidence }).ToList(),
    };

    public static KnowledgeAssessment NoClaims() => new() { Confidence = Confidence.Unassessed };
}

internal sealed class FakeReviewAgent : IKnowledgeReviewAgent
{
    private readonly Queue<KnowledgeReview> _verdicts;
    public int CallCount { get; private set; }
    public FakeReviewAgent(params KnowledgeReview[] verdicts) => _verdicts = new Queue<KnowledgeReview>(verdicts);

    public Task<KnowledgeReview> ReviewAsync(KnowledgeRequest request, string candidateAnswer, string sourceLabel, CancellationToken ct = default)
    {
        CallCount++;
        var verdict = _verdicts.Count > 0 ? _verdicts.Dequeue()
            : new KnowledgeReview(true, Confidence.Of(0.7), Array.Empty<string>(), "default");
        return Task.FromResult(verdict);
    }

    public static KnowledgeReview Accept(double conf = 0.8) =>
        new(true, Confidence.Of(conf), Array.Empty<string>(), "looks good");
    public static KnowledgeReview Reject(double conf = 0.2, params string[] missing) =>
        new(false, Confidence.Of(conf), missing, "insufficient");
}

internal sealed class FakeCompilerAgent : IKnowledgeCompilerAgent
{
    private readonly KnowledgeResponse _response;
    public string? CompiledFrom { get; private set; }
    public FakeCompilerAgent(KnowledgeResponse response) => _response = response;

    public Task<KnowledgeResponse> CompileAsync(KnowledgeRequest request, string rawFindings, IReadOnlyList<ResearchCitation> citations, CancellationToken ct = default)
    {
        CompiledFrom = rawFindings;
        return Task.FromResult(_response with { Citations = citations.Count > 0 ? citations : _response.Citations });
    }
}

internal sealed class FakeOrchestrator : IDeepResearchOrchestrator
{
    private readonly ResearchOutcome _outcome;
    public bool DeepResearchCalled { get; private set; }
    public string? LastQuestion { get; private set; }
    public FakeOrchestrator(ResearchOutcome outcome) => _outcome = outcome;

    public Task<ResearchOutcome> RunDeepResearchAsync(string question, string userId, CancellationToken ct = default)
    {
        DeepResearchCalled = true;
        LastQuestion = question;
        return Task.FromResult(_outcome);
    }

    public Task<ResearchOutcome> RunResearchAsync(string question, string userId, ResearchOrchestrationMode mode, CancellationToken ct = default)
    {
        DeepResearchCalled = true;
        LastQuestion = question;
        return Task.FromResult(_outcome);
    }
}

/// <summary>Fake Ollama toolbox: returns a canned generation, or throws if configured to.</summary>
internal sealed class FakeToolbox : IResearchToolbox
{
    private readonly string _generation;
    private readonly bool _throwOnGenerate;
    public FakeToolbox(string generation = "", bool throwOnGenerate = false)
    {
        _generation = generation;
        _throwOnGenerate = throwOnGenerate;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        => _throwOnGenerate ? throw new InvalidOperationException("ollama down") : Task.FromResult(_generation);
    public Task<List<float>> GetEmbeddingAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new List<float>());
    public Task<string> SearchWebAsync(string query, CancellationToken ct = default)
        => Task.FromResult("");
}
