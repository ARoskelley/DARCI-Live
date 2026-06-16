using Xunit;

namespace DammProject.Tests;

public class BehaviorCheck
{
    [Fact]
    public void ComputeCheckDigit_ThrowsArgumentException_ForNonDigitCharacters()
    {
        Assert.Throws<ArgumentException>(() => DammValidator.ComputeCheckDigit("572a"));
    }

    [Fact]
    public void IsValid_ReturnsFalse_ForEmptyString()
    {
        Assert.False(DammValidator.IsValid(""));
    }
}