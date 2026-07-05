#nullable enable

using Darci.Nodes;
using Darci.Research.Agents;

namespace Darci.Research.Agents.Tests;

/// <summary>Eligibility is a pure gate that PROPOSES a campaign; it never flips state (§14a).</summary>
public class CampaignEligibilityTests
{
    private static readonly CampaignEligibilityOptions Opt = new() { MinDistinctSuccesses = 2, MaxFailures = 1 };

    private static InnovatedKnowledgeRecord Entry(Provenance p = Provenance.Innovated) =>
        new() { Hypothesis = "h", Provenance = p, Confidence = Confidence.Of(0.3) };

    [Fact]
    public void Eligible_WhenInnovated_WithEnoughDistinctSuccesses()
    {
        var r = CampaignEligibilityEvaluator.Evaluate(Entry(), (Successes: 2, Failures: 0), Opt);
        Assert.True(r.Eligible);
    }

    [Fact]
    public void NotEligible_WhenTooFewSuccesses()
        => Assert.False(CampaignEligibilityEvaluator.Evaluate(Entry(), (1, 0), Opt).Eligible);

    [Fact]
    public void NotEligible_WhenTooManyFailures()
        => Assert.False(CampaignEligibilityEvaluator.Evaluate(Entry(), (5, 2), Opt).Eligible);

    [Theory]
    [InlineData(Provenance.UnderTest)]
    [InlineData(Provenance.ProvisionallyValidated)]
    [InlineData(Provenance.Retracted)]
    public void NotEligible_WhenNotAtInnovatedStage(Provenance p)
        => Assert.False(CampaignEligibilityEvaluator.Evaluate(Entry(p), (5, 0), Opt).Eligible);
}
