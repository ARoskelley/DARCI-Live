#nullable enable

namespace Darci.Coding.Tests;

/// <summary>Smoke tests that verify the Darci.Coding project reference resolves.</summary>
public class CodingModelsTests
{
    [Fact]
    public void CodingTaskRecord_DefaultConfidenceScore_IsNegativeOne()
    {
        var record = new CodingTaskRecord();
        Assert.Equal(-1.0, record.ConfidenceScore);
    }

    [Fact]
    public void CodingTaskStatusResponse_DefaultVerificationResult_IsEmpty()
    {
        var response = new CodingTaskStatusResponse();
        Assert.Equal("", response.VerificationResult);
    }
}
