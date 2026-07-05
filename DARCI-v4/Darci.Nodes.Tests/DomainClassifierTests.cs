using Darci.Nodes;

namespace Darci.Nodes.Tests;

/// <summary>Sub-unit 1 domain classifier (deliberately simple: explicit tag wins, else keyword scan).</summary>
public class DomainClassifierTests
{
    [Fact]
    public void ExplicitSensitiveTag_Wins()
        => Assert.Equal(KnowledgeDomain.Sensitive, DomainClassifier.Classify("sensitive", "make a to-do list"));

    [Fact]
    public void ExplicitGeneralTag_Wins_EvenOverKeyword()
        => Assert.Equal(KnowledgeDomain.General, DomainClassifier.Classify("general", "cardiac implant dosing"));

    [Theory]
    [InlineData("design a myoelectric prosthetic grip")]
    [InlineData("optimal drug dosage for the patient")]
    [InlineData("load-bearing structural bracket under fatigue")]
    [InlineData("flight control actuator torque budget")]
    public void SensitiveKeywords_ClassifySensitive(string text)
        => Assert.Equal(KnowledgeDomain.Sensitive, DomainClassifier.Classify(null, text));

    [Theory]
    [InlineData("write a markdown parser")]
    [InlineData("cache the API responses in memory")]
    [InlineData("sort this list of names alphabetically")]
    public void BenignText_ClassifiesGeneral(string text)
        => Assert.Equal(KnowledgeDomain.General, DomainClassifier.Classify(null, text));

    [Fact]
    public void EmptyInput_DefaultsGeneral()
        => Assert.Equal(KnowledgeDomain.General, DomainClassifier.Classify(null));

    [Fact]
    public void UnknownTag_FallsThroughToKeywordScan()
    {
        Assert.Equal(KnowledgeDomain.Sensitive, DomainClassifier.Classify("weird-tag", "surgical implant"));
        Assert.Equal(KnowledgeDomain.General, DomainClassifier.Classify("weird-tag", "rename a variable"));
    }

    [Fact]
    public void ScansAcrossMultipleTextSignals()
        => Assert.Equal(KnowledgeDomain.Sensitive, DomainClassifier.Classify(null, "improve throughput", "for a cardiac monitor", null));
}
