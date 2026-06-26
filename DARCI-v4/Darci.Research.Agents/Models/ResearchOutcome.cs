#nullable enable

using Darci.Nodes;

namespace Darci.Research.Agents.Models;

public sealed record ResearchOutcome
{
    /// <summary>Research is considered uncertain below this confidence (preserved from the legacy field).</summary>
    public const double UncertaintyThreshold = 0.45;

    public bool IsSuccess { get; init; }
    public string SessionId { get; init; } = "";
    public string Question { get; init; } = "";
    public string FinalAnswer { get; init; } = "";

    /// <summary>Unified confidence for this outcome (replaces the former float Confidence).</summary>
    public Confidence Confidence { get; init; } = Confidence.Unassessed;

    public IReadOnlyList<AgentReport> AgentReports { get; init; } = Array.Empty<AgentReport>();
    public IReadOnlyList<ResearchCitation> Citations { get; init; } = Array.Empty<ResearchCitation>();
    public string? Error { get; init; }

    /// <summary>
    /// Derived (no longer a stored field): uncertain when unassessed or below the research threshold.
    /// Preserves the prior behaviour exactly (was <c>Confidence &lt; 0.45f</c>).
    /// </summary>
    public bool IsUncertain => !Confidence.IsAssessed || Confidence.Score < UncertaintyThreshold;

    public static ResearchOutcome Failed(string question, string error = "No successful research agents completed.")
        => new()
        {
            IsSuccess = false,
            Question = question,
            Error = error,
            Confidence = Confidence.Unassessed,   // unassessed ⇒ IsUncertain == true
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
            Confidence = assessment.Confidence,
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
