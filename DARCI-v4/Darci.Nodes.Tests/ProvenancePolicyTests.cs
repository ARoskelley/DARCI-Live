using Darci.Nodes;

namespace Darci.Nodes.Tests;

public class ProvenancePolicyTests
{
    [Theory]
    [InlineData(Provenance.Verified, true)]
    [InlineData(Provenance.Researched, true)]
    [InlineData(Provenance.Innovated, false)]
    [InlineData(Provenance.UnderTest, false)]
    [InlineData(Provenance.ProvisionallyValidated, false)]
    [InlineData(Provenance.Unverified, false)]
    [InlineData(Provenance.Retracted, false)]
    public void IsTrustedAsFact_OnlyVerifiedAndResearched(Provenance p, bool trusted)
    {
        Assert.Equal(trusted, ProvenancePolicy.IsTrustedAsFact(p));
    }

    [Theory]
    [InlineData(Provenance.Innovated)]
    [InlineData(Provenance.UnderTest)]
    [InlineData(Provenance.ProvisionallyValidated)]
    [InlineData(Provenance.Unverified)]
    public void Clamp_CappedProvenance_NeverExceedsCap_AndStaysLow(Provenance p)
    {
        var clamped = ProvenancePolicy.Clamp(p, Confidence.Of(0.95));
        Assert.True(clamped.Score <= ProvenancePolicy.InnovatedCap);
        Assert.True(clamped.IsLow);   // a hypothesis can never read as a confident fact
    }

    [Fact]
    public void Clamp_PreservesLowScoresAndNote()
    {
        var clamped = ProvenancePolicy.Clamp(Provenance.Innovated, Confidence.Of(0.2, "tentative"));
        Assert.Equal(0.2, clamped.Score, 5);
        Assert.Equal("tentative", clamped.Note);
    }

    [Fact]
    public void Clamp_TrustedProvenance_IsNotCapped()
    {
        var clamped = ProvenancePolicy.Clamp(Provenance.Researched, Confidence.Of(0.9));
        Assert.Equal(0.9, clamped.Score, 5);
    }

    [Fact]
    public void Clamp_Retracted_IsExcluded()
    {
        var clamped = ProvenancePolicy.Clamp(Provenance.Retracted, Confidence.Of(0.9));
        Assert.False(clamped.IsAssessed);   // unusable
    }
}
