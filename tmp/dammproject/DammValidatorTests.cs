using Xunit;

namespace DammProject.Tests;

public class DammValidatorTests
{
    [Fact]
    public void ComputeCheckDigit_Returns4_For572()
    {
        Assert.Equal(4, DammValidator.ComputeCheckDigit("572"));
    }

    [Fact]
    public void IsValid_ReturnsTrue_For5724()
    {
        Assert.True(DammValidator.IsValid("5724"));
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenCheckDigitIsWrong()
    {
        Assert.False(DammValidator.IsValid("5721"));
    }

    [Fact]
    public void ComputeCheckDigit_Returns9_For43881234567()
    {
        Assert.Equal(9, DammValidator.ComputeCheckDigit("43881234567"));
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenInputIsNull()
    {
        Assert.Throws<ArgumentException>(() => DammValidator.IsValid(null));
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenInputIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => DammValidator.IsValid(""));
    }
}