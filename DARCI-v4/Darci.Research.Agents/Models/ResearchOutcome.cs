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
}

public sealed record ResearchCitation
{
    public int Number { get; init; }
    public string AgentType { get; init; } = "";
    public string SubQuestion { get; init; } = "";
    public string? SourceRef { get; init; }
    public float Confidence { get; init; }
}
