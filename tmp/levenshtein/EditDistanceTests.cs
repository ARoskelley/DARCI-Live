using Xunit;

namespace Levenshtein.Tests;

public class EditDistanceTests
{
    [Fact]
    public void Distance_BothEmpty_ReturnsZero()
    {
        Assert.Equal(0, EditDistance.Distance("", ""));
    }

    [Fact]
    public void Distance_Identical_ReturnsZero()
    {
        Assert.Equal(0, EditDistance.Distance("hello", "hello"));
    }

    [Fact]
    public void Distance_EmptyToWord_ReturnsWordLength()
    {
        Assert.Equal(3, EditDistance.Distance("", "abc"));
    }

    [Fact]
    public void Distance_WordToEmpty_ReturnsWordLength()
    {
        Assert.Equal(3, EditDistance.Distance("abc", ""));
    }

    [Fact]
    public void Distance_KittenToSitting_ReturnsThree()
    {
        // classic example: kitten → sitten → sittin → sitting
        Assert.Equal(3, EditDistance.Distance("kitten", "sitting"));
    }

    [Fact]
    public void Distance_SundayToSaturday_ReturnsThree()
    {
        Assert.Equal(3, EditDistance.Distance("sunday", "saturday"));
    }
}
