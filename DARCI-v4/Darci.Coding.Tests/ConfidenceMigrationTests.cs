#nullable enable

using Darci.Nodes;
using Darci.Research.Agents.Models;

namespace Darci.Coding.Tests;

// ── Coding decision point: proactive low-confidence research ──────────────────
// Verifies the unified Confidence type preserves the prior behaviour at the coding
// decision point (was: confidence >= 0 && confidence < 0.4 && note not blank && note != "nothing").

public class CodingLowConfidenceResearchTests
{
    [Fact]
    public void LowConfidence_WithConcreteNote_TriggersResearch()
    {
        var c = Confidence.Of(0.2, "the Damm quasigroup table");
        Assert.True(CodingAgentLoop.ShouldResearchOnLowConfidence(c));
    }

    [Fact]
    public void HighConfidence_DoesNotTriggerResearch()
    {
        var c = Confidence.Of(0.9, "the Damm quasigroup table");
        Assert.False(CodingAgentLoop.ShouldResearchOnLowConfidence(c));
    }

    [Theory]
    [InlineData(0.39, true)]   // just below the 0.4 threshold
    [InlineData(0.4, false)]   // at threshold = not low
    public void Threshold_PreservedAtPointFour(double score, bool expected)
    {
        var c = Confidence.Of(score, "something");
        Assert.Equal(expected, CodingAgentLoop.ShouldResearchOnLowConfidence(c));
    }

    [Fact]
    public void Unassessed_DoesNotTriggerResearch()
    {
        // Unassessed is a gap but not "low" — and there is no note to research on.
        Assert.False(CodingAgentLoop.ShouldResearchOnLowConfidence(Confidence.Unassessed));
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("Nothing")]
    [InlineData("")]
    [InlineData("   ")]
    public void LowConfidence_WithoutConcreteNote_DoesNotTriggerResearch(string note)
    {
        var c = Confidence.Of(0.2, note);
        Assert.False(CodingAgentLoop.ShouldResearchOnLowConfidence(c));
    }
}

// ── Research decision point: ResearchOutcome.IsUncertain ──────────────────────
// The research uncertainty threshold (0.45) is distinct from the coding low threshold (0.4)
// and must be preserved exactly by the derived IsUncertain property.

public class ResearchOutcomeConfidenceTests
{
    [Theory]
    [InlineData(0.44, true)]   // below research threshold → uncertain
    [InlineData(0.45, false)]  // at threshold → certain
    [InlineData(0.9, false)]
    public void IsUncertain_PreservesPointFourFiveThreshold(double score, bool expectedUncertain)
    {
        var outcome = new ResearchOutcome { Confidence = Confidence.Of(score) };
        Assert.Equal(expectedUncertain, outcome.IsUncertain);
    }

    [Fact]
    public void Failed_IsUncertain()
    {
        var outcome = ResearchOutcome.Failed("q");
        Assert.True(outcome.IsUncertain);          // unassessed ⇒ uncertain
        Assert.False(outcome.Confidence.IsAssessed);
    }

    [Fact]
    public void FromAssessment_CarriesAssessmentConfidence()
    {
        var assessment = new KnowledgeAssessment { Confidence = Confidence.Of(0.7) };
        var outcome = ResearchOutcome.FromAssessment(assessment, "q");
        Assert.Equal(0.7, outcome.Confidence.Score, 5);
        Assert.False(outcome.IsUncertain);          // 0.7 >= 0.45
    }

    [Fact]
    public void KnowledgeAssessment_DefaultConfidence_IsUnassessedGap()
    {
        var assessment = new KnowledgeAssessment();
        Assert.False(assessment.Confidence.IsAssessed);
        Assert.True(assessment.Confidence.IsGap);
    }
}
