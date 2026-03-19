#nullable enable

namespace Darci.Research.Agents.Models;

public sealed record AgentReport
{
    public string JobId { get; init; } = "";
    public string AgentType { get; init; } = "";
    public string SubQuestion { get; init; } = "";
    public bool IsSuccess { get; init; }
    public string Summary { get; init; } = "";
    public float Confidence { get; init; }
    public string? SourceRef { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}
