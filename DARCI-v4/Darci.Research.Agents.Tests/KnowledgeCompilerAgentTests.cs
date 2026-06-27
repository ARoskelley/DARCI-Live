#nullable enable

using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

public class KnowledgeCompilerAgentTests
{
    private static OllamaKnowledgeCompilerAgent Agent(string generation, bool throwOnGenerate = false) =>
        new(new FakeToolbox(generation, throwOnGenerate), NullLogger<OllamaKnowledgeCompilerAgent>.Instance);

    private static readonly IReadOnlyList<ResearchCitation> NoCitations = Array.Empty<ResearchCitation>();
    private static KnowledgeRequest Req() => new("How do I implement Damm?", Kind: KnowledgeKind.HowTo);

    [Fact]
    public async Task WellFormedJson_ProducesStructuredResponse()
    {
        var json = """
        {"directAnswer":"Use the standard Damm quasigroup table.",
         "findings":["interim starts at 0","result 0 means valid"],
         "steps":["index table[interim][digit] per char","return final interim"],
         "examples":[{"summary":"572 -> check digit 4","source":"wikipedia"}],
         "gaps":[]}
        """;
        var resp = await Agent(json).CompileAsync(Req(), "raw notes", NoCitations);

        Assert.Equal("Use the standard Damm quasigroup table.", resp.DirectAnswer);
        Assert.Equal(2, resp.Findings.Count);
        Assert.Equal(2, resp.Steps.Count);
        Assert.Single(resp.Examples);
        Assert.Equal("572 -> check digit 4", resp.Examples[0].Summary);
        Assert.Empty(resp.Gaps);
    }

    [Fact]
    public async Task JsonWrappedInProseAndFences_IsStillExtracted()
    {
        var noisy = "Sure! Here is the compiled result:\n```json\n{\"directAnswer\":\"X\",\"findings\":[\"a\"],\"steps\":[],\"gaps\":[]}\n```\nHope that helps!";
        var resp = await Agent(noisy).CompileAsync(Req(), "raw", NoCitations);

        Assert.Equal("X", resp.DirectAnswer);
        Assert.Single(resp.Findings);
    }

    [Fact]
    public async Task ProseBlob_FallsBackToStructured_NeverPlainProse()
    {
        // The exact failure mode Phase 2 is meant to kill: a rambling non-JSON answer.
        var prose = "I think you should check your SDK version. Also try updating your NuGet packages.";
        var resp = await Agent(prose).CompileAsync(Req(), prose, NoCitations);

        Assert.NotEmpty(resp.Findings);                                  // still structured
        Assert.Contains(resp.Gaps, g => g.Contains("could not structure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompilerUnavailable_FallsBackToStructured()
    {
        var resp = await Agent("", throwOnGenerate: true).CompileAsync(Req(), "fact one. fact two", NoCitations);
        Assert.NotEmpty(resp.Findings);
        Assert.Contains(resp.Gaps, g => g.Contains("could not structure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EmptyRawFindings_ReturnsUnanswered()
    {
        var resp = await Agent("{}").CompileAsync(Req(), "   ", NoCitations);
        Assert.False(resp.Answered);
        Assert.NotEmpty(resp.Gaps);
    }

    [Fact]
    public void Fallback_SplitsRawIntoFindings()
    {
        var resp = OllamaKnowledgeCompilerAgent.Fallback("first fact. second fact\n- third fact", NoCitations);
        Assert.True(resp.Findings.Count >= 3);
        Assert.Equal("first fact", resp.DirectAnswer);
    }
}
