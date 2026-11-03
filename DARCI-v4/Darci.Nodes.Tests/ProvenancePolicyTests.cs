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
    [InlineData(Provenance.Unverified)]
    public void Clamp_BottomStages_NeverExceedInnovatedCap_AndStayLow(Provenance p)
    {
        var clamped = ProvenancePolicy.Clamp(p, Confidence.Of(0.95));
        Assert.True(clamped.Score <= ProvenancePolicy.InnovatedCap);
        Assert.True(clamped.IsLow);   // a fresh/under-test hypothesis can never read as a confident fact
    }

    [Fact]
    public void Clamp_ProvisionallyValidated_MidTierCap_ByDomain()
    {
        // §4a: a human-authorized campaign that passed lifts the cap to a mid tier — 0.6 general, 0.45 sensitive.
        var general = ProvenancePolicy.Clamp(Provenance.ProvisionallyValidated, Confidence.Of(0.95), KnowledgeDomain.General);
        Assert.Equal(ProvenancePolicy.ProvisionalCapGeneral, general.Score, 5);
        Assert.False(general.IsLow);   // actionable, but still not "fact"
        Assert.False(ProvenancePolicy.IsTrustedAsFact(Provenance.ProvisionallyValidated));

        var sensitive = ProvenancePolicy.Clamp(Provenance.ProvisionallyValidated, Confidence.Of(0.95), KnowledgeDomain.Sensitive);
        Assert.Equal(ProvenancePolicy.ProvisionalCapSensitive, sensitive.Score, 5);
        Assert.True(sensitive.Score < general.Score);   // sensitive earns a strictly lower ceiling
    }

    [Fact]
    public void Clamp_HumanApproved_IsUncapped()
    {
        // The trusted tier (human, both domains) is not capped — the one above-cap path.
        var clamped = ProvenancePolicy.Clamp(Provenance.HumanApproved, Confidence.Of(0.9));
        Assert.Equal(0.9, clamped.Score, 5);
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
