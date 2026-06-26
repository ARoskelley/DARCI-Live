using Xunit;

namespace DammProject.Tests;

public class DammValidatorTests
{
    // Classic example from the Damm algorithm reference: 572 has check digit 4
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
    public void IsValid_ReturnsFalse_ForAnotherIncorrectCheckDigit()
    {
        Assert.False(DammValidator.IsValid("5727"));
    }

    // Longer sequence: 43881234567 has check digit 9
    [Fact]
    public void ComputeCheckDigit_Returns9_For43881234567()
    {
        Assert.Equal(9, DammValidator.ComputeCheckDigit("43881234567"));
    }

    // Additional test to ensure the check digit is correctly computed
    [Fact]
    public void ComputeCheckDigit_Returns0_ForValidNumber()
    {
        Assert.Equal(0, DammValidator.ComputeCheckDigit("438812345679"));
    }
}