#nullable enable

namespace Darci.Research.Agents.Models;

public sealed record ResearchOutcome
{
    public bool IsSuccess { get; init; }
    public string SessionId { get; init; } = "";
    public string Question { get; init; } = "";
    public string FinalAnswer { get; init; } = "";
    public float Confidence { get; init; }
    public IReadOnlyList<AgentReport> AgentReports { get; init; } = Array.Empty<AgentReport>();
    public IReadOnlyList<ResearchCitation> Citations { get; init; } = Array.Empty<ResearchCitation>();
    public bool IsUncertain { get; init; }
    public string? Error { get; init; }

    public static ResearchOutcome Failed(string question, string error = "No successful research agents completed.")
        => new()
        {
            IsSuccess = false,
            Question = question,
            Error = error,
            IsUncertain = true
        };

    /// <summary>
    /// Creates a successful outcome directly from a knowledge assessment
    /// when agents were skipped (confidence was sufficient).
    /// </summary>
    public static ResearchOutcome FromAssessment(
        KnowledgeAssessment assessment, string question)
        => new()
        {
            IsSuccess = true,
            Question = question,
            FinalAnswer = string.Join("\n",
                assessment.SupportingClaims.Take(5).Select(c => c.Statement)),
            Confidence = assessment.GraphConfidence,
            IsUncertain = assessment.GraphConfidence < 0.45f,
        };
}

public sealed record ResearchCitation
{
    public int Number { get; init; }
    public string AgentType { get; init; } = "";
    public string SubQuestion { get; init; } = "";
    public string? SourceRef { get; init; }
    public float Confidence { get; init; }
}
