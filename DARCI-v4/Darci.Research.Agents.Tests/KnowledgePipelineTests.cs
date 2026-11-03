#nullable enable

using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

public class KnowledgePipelineTests
{
    private static KnowledgePipeline Build(
        FakeAssessor assessor, FakeReviewAgent review, FakeCompilerAgent compiler, FakeOrchestrator orch,
        int escalateAfter = 1) =>
        new(assessor, review, compiler, orch,
            new KnowledgePipelineOptions { EscalateAfterReviewFailures = escalateAfter },
            NullLogger<KnowledgePipeline>.Instance);

    private static KnowledgeResponse SampleCompiled() => new()
    {
        DirectAnswer = "Use the correct Damm quasigroup table.",
        Findings = new[] { "The interim digit starts at 0.", "Result 0 means valid." },
        Steps = new[] { "Index table[interim][digit] per character." },
    };

    private static ResearchOutcome ResearchAnswer() => new()
    {
        IsSuccess = true,
        FinalAnswer = "Damm uses a specific 10x10 weakly totally anti-symmetric quasigroup.",
        Confidence = Confidence.Of(0.6),
    };

    [Fact]
    public async Task ReviewAccept_UsesKg_SkipsDeepResearch()
    {
        var assessor = new FakeAssessor(FakeAssessor.WithClaims(0.8, "Damm is a check-digit algorithm."));
        var review = new FakeReviewAgent(FakeReviewAgent.Accept(0.85), FakeReviewAgent.Accept(0.85)); // r1, r2
        var compiler = new FakeCompilerAgent(SampleCompiled());
        var orch = new FakeOrchestrator(ResearchAnswer());

        var resp = await Build(assessor, review, compiler, orch).RunAsync(new KnowledgeRequest("What is Damm?"));

        Assert.False(orch.DeepResearchCalled);                 // KG was sufficient — no escalation
        Assert.True(resp.Answered);
        Assert.True(resp.Confidence.IsAssessed);
        Assert.Equal("Damm is a check-digit algorithm.", compiler.CompiledFrom); // compiled the KG text
        Assert.NotEmpty(resp.Findings);
    }

    [Fact]
    public async Task ReviewReject_EscalatesToDeepResearch()
    {
        var assessor = new FakeAssessor(FakeAssessor.WithClaims(0.5, "Damm is something vague."));
        var review = new FakeReviewAgent(FakeReviewAgent.Reject(0.2, "no table"), FakeReviewAgent.Accept(0.8)); // r1 reject, r2 accept
        var compiler = new FakeCompilerAgent(SampleCompiled());
        var orch = new FakeOrchestrator(ResearchAnswer());

        var resp = await Build(assessor, review, compiler, orch).RunAsync(new KnowledgeRequest("Damm table?"));

        Assert.True(orch.DeepResearchCalled);                  // review rejected KG → escalated
        Assert.True(resp.Answered);
        Assert.Contains("quasigroup", compiler.CompiledFrom);  // compiled the research synthesis, not the KG text
    }

    [Fact]
    public async Task NoKgClaims_EscalatesDirectly()
    {
        var assessor = new FakeAssessor(FakeAssessor.NoClaims());
        var review = new FakeReviewAgent(FakeReviewAgent.Accept(0.7)); // only review #2 runs (no KG to review)
        var compiler = new FakeCompilerAgent(SampleCompiled());
        var orch = new FakeOrchestrator(ResearchAnswer());

        var resp = await Build(assessor, review, compiler, orch).RunAsync(new KnowledgeRequest("obscure topic"));

        Assert.True(orch.DeepResearchCalled);
        Assert.Equal(1, review.CallCount);                     // review #1 skipped (no KG candidate)
        Assert.True(resp.Answered);
    }

    [Fact]
    public async Task FinalReviewRejects_MarksGaps_NotAnswered()
    {
        var assessor = new FakeAssessor(FakeAssessor.WithClaims(0.5, "partial info"));
        var review = new FakeReviewAgent(
            FakeReviewAgent.Reject(0.2, "no table"),                       // r1 → escalate
            FakeReviewAgent.Reject(0.3, "missing edge cases", "no examples")); // r2 → gaps
        var compiler = new FakeCompilerAgent(SampleCompiled());
        var orch = new FakeOrchestrator(ResearchAnswer());

        var resp = await Build(assessor, review, compiler, orch).RunAsync(new KnowledgeRequest("Damm edge cases?"));

        Assert.False(resp.Answered);
        Assert.Contains("missing edge cases", resp.Gaps);
        Assert.Contains("no examples", resp.Gaps);
    }

    [Fact]
    public async Task EscalateThreshold_Two_DoesNotEscalateOnSingleFailure()
    {
        // With threshold 2 and a single KG source, one review failure does not escalate — the pipeline
        // proceeds with the KG text and lets review #2 flag gaps (no research spend).
        var assessor = new FakeAssessor(FakeAssessor.WithClaims(0.5, "kg only answer"));
        var review = new FakeReviewAgent(FakeReviewAgent.Reject(0.2, "weak"), FakeReviewAgent.Accept(0.6));
        var compiler = new FakeCompilerAgent(SampleCompiled());
        var orch = new FakeOrchestrator(ResearchAnswer());

        var resp = await Build(assessor, review, compiler, orch, escalateAfter: 2).RunAsync(new KnowledgeRequest("q"));

        Assert.False(orch.DeepResearchCalled);                 // tolerated the failure, no escalation
        Assert.Equal("kg only answer", compiler.CompiledFrom);
    }

    [Fact]
    public void MostConservative_PrefersLowerAssessedOrTheAssessedOne()
    {
        Assert.Equal(0.3, KnowledgePipeline.MostConservative(Confidence.Of(0.3), Confidence.Of(0.9)).Score, 5);
        Assert.Equal(0.4, KnowledgePipeline.MostConservative(Confidence.Unassessed, Confidence.Of(0.4)).Score, 5);
        Assert.Equal(0.4, KnowledgePipeline.MostConservative(Confidence.Of(0.4), Confidence.Unassessed).Score, 5);
    }
}
