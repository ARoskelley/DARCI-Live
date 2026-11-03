#nullable enable

using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;

namespace Darci.Research.Agents.Tests;

public class InnovationSynthesizerTests
{
    [Fact]
    public void Parse_WellFormedSolvable_ProducesCappedProposal()
    {
        var json = """
        {"solvable": true,
         "hypothesis": "bridge sensor X to controller Y via adapter Z",
         "reasoning": [{"inference":"X and Y share protocol P","citedFacts":["f1","f2"]}],
         "assumptions": ["Z is available"],
         "requiredExternalInputs": []}
        """;
        var p = OllamaInnovationSynthesizer.Parse(json);

        Assert.NotNull(p);
        Assert.Equal(ProposalStatus.Proposed, p!.Status);
        Assert.Equal("bridge sensor X to controller Y via adapter Z", p.Hypothesis);
        Assert.Single(p.Reasoning);
        Assert.Equal(new[] { "f1", "f2" }, p.Reasoning[0].CitedFacts);
        Assert.Equal(Provenance.Innovated, p.Provenance);
        Assert.True(p.Confidence.IsLow);                              // capped — never a fact
        Assert.True(p.Confidence.Score <= ProvenancePolicy.InnovatedCap);
    }

    [Fact]
    public void Parse_Unsolvable_CarriesRequiredExternalInputs()
    {
        var json = """
        {"solvable": false, "hypothesis": "",
         "reasoning": [], "assumptions": [],
         "requiredExternalInputs": ["measured EMG latency for sensor X", "a biocompatibility test"]}
        """;
        var p = OllamaInnovationSynthesizer.Parse(json);

        Assert.NotNull(p);
        Assert.Equal(ProposalStatus.Unsolvable, p!.Status);
        Assert.Contains("measured EMG latency for sensor X", p.RequiredExternalInputs);
        Assert.False(p.Confidence.IsAssessed);
    }

    [Fact]
    public void Parse_SolvableButEmptyHypothesis_IsTreatedAsUnsolvable()
    {
        var p = OllamaInnovationSynthesizer.Parse("""{"solvable": true, "hypothesis": "   "}""");
        Assert.Equal(ProposalStatus.Unsolvable, p!.Status);
    }

    [Fact]
    public void Parse_JsonWrappedInProse_IsExtracted()
    {
        var noisy = "Here you go:\n```json\n{\"solvable\":true,\"hypothesis\":\"H\"}\n```\ndone";
        var p = OllamaInnovationSynthesizer.Parse(noisy);
        Assert.Equal(ProposalStatus.Proposed, p!.Status);
        Assert.Equal("H", p.Hypothesis);
    }

    [Fact]
    public void Parse_NonJson_ReturnsNull()
    {
        Assert.Null(OllamaInnovationSynthesizer.Parse("I think you should just try harder."));
    }
}
