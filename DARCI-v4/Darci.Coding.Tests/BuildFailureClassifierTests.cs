#nullable enable

namespace Darci.Coding.Tests;

public class BuildFailureClassifierTests
{
    private const string Cs0101 =
        @"C:\ws\tmp\levenshtein\levenshtein\Tests\EditDistance.cs(5,14): error CS0101: The namespace 'Levenshtein.Tests' already contains a definition for 'EditDistance' [C:\ws\tmp\levenshtein\Levenshtein.csproj]";

    private const string Cs0111 =
        @"DammValidatorTests.cs(26,17): error CS0111: Type 'DammValidatorTests' already defines a member called 'IsValid_ReturnsFalse_ForAnotherIncorrectCheckDigit' with the same parameter types [C:\ws\DammProject.csproj]";

    [Fact]
    public void Cs0101_ClassifiedAsSelfInflictedDuplicate()
    {
        var result = BuildFailureClassifier.Classify(Cs0101);

        Assert.Equal(BuildFailureClass.SelfInflictedDuplicate, result.Class);
        Assert.False(result.ShouldResearch);
        Assert.Contains("EditDistance", result.DuplicateSymbols);
        Assert.Contains(result.DuplicatePaths, p => p.Contains("EditDistance.cs"));
        Assert.NotNull(result.TargetedGuidance);
        Assert.Contains("Do NOT emit", result.TargetedGuidance!);
        Assert.Contains("EditDistance", result.TargetedGuidance!);
    }

    [Fact]
    public void Cs0111_ClassifiedAsSelfInflictedDuplicate()
    {
        var result = BuildFailureClassifier.Classify(Cs0111);

        Assert.Equal(BuildFailureClass.SelfInflictedDuplicate, result.Class);
        Assert.False(result.ShouldResearch);
        Assert.Contains("IsValid_ReturnsFalse_ForAnotherIncorrectCheckDigit", result.DuplicateSymbols);
    }

    [Fact]
    public void TestAssertionFailure_ClassifiedAsKnowledgeGap()
    {
        const string assertionFailure =
            "Failed! - Assert.Equal() Failure\nExpected: 4\nActual: 7\n  DammProject.Tests.ComputeCheckDigit_Returns4_For572";
        var result = BuildFailureClassifier.Classify(assertionFailure);

        Assert.Equal(BuildFailureClass.KnowledgeGap, result.Class);
        Assert.True(result.ShouldResearch);
        Assert.Null(result.TargetedGuidance);
    }

    [Fact]
    public void GenericCompileError_NotDuplicate_IsKnowledgeGap()
    {
        const string other = "Program.cs(10,5): error CS0246: The type or namespace name 'Foo' could not be found";
        var result = BuildFailureClassifier.Classify(other);

        Assert.Equal(BuildFailureClass.KnowledgeGap, result.Class);
        Assert.True(result.ShouldResearch);
    }

    [Fact]
    public void MultipleDuplicates_AllCaptured()
    {
        var combined = Cs0101 + "\n" + Cs0111;
        var result = BuildFailureClassifier.Classify(combined);

        Assert.Equal(BuildFailureClass.SelfInflictedDuplicate, result.Class);
        Assert.Equal(2, result.DuplicateSymbols.Count);
        Assert.Equal(2, result.DuplicatePaths.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOutput_DefaultsToKnowledgeGap(string? output)
    {
        var result = BuildFailureClassifier.Classify(output);
        Assert.Equal(BuildFailureClass.KnowledgeGap, result.Class);
        Assert.True(result.ShouldResearch);
    }
}
