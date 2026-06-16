using Xunit;

namespace DammProject.Tests;

public class BehaviorCheck
{
    [Fact]
    public void IsValid_ReturnsCorrectValue()
    {
        Assert.True(DammValidator.IsValid("1234567890"));
        Assert.False(DammValidator.IsValid("123456789A"));
        Assert.False(DammValidator.IsValid(""));
    }
}