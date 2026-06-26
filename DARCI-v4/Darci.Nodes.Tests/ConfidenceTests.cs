using Darci.Nodes;

namespace Darci.Nodes.Tests;

public class ConfidenceTests
{
    [Fact]
    public void Unassessed_IsAGap_ButNotLow()
    {
        var c = Confidence.Unassessed;
        Assert.False(c.IsAssessed);
        Assert.True(c.IsGap);        // never assessed counts as a gap
        Assert.False(c.IsLow);       // "low" requires being assessed
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.2, true)]
    [InlineData(0.39, true)]
    [InlineData(0.4, false)]
    [InlineData(0.9, false)]
    public void IsLow_TracksThreshold(double score, bool expectedLow)
    {
        var c = Confidence.Of(score);
        Assert.True(c.IsAssessed);
        Assert.Equal(expectedLow, c.IsLow);
        Assert.Equal(expectedLow, c.IsGap);   // assessed → gap iff low
    }

    [Theory]
    [InlineData(-5.0, -1.0)]   // negatives normalize to the unassessed sentinel
    [InlineData(1.5, 1.0)]     // clamps high
    [InlineData(0.5, 0.5)]
    public void Of_NormalizesScore(double input, double expected)
    {
        Assert.Equal(expected, Confidence.Of(input).Score, 5);
    }

    [Fact]
    public void Note_IsPreserved()
    {
        var c = Confidence.Of(0.3, "unsure about the table");
        Assert.Equal("unsure about the table", c.Note);
        Assert.True(c.IsLow);
    }
}
