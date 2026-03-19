#nullable enable

namespace Darci.Memory.Confidence.Models;

public sealed record KnowledgeClaim
{
    public string Id { get; init; } = "";
    public string Statement { get; init; } = "";
    public string Domain { get; init; } = "general";
    public string[] EntityIds { get; init; } = Array.Empty<string>();
    public string[] RelationIds { get; init; } = Array.Empty<string>();
    public float Confidence { get; init; } = 0.5f;
    public float SourceQuality { get; init; } = 0.5f;
    public float Corroboration { get; init; }
    public float Contradiction { get; init; }
    public float RecencyWeight { get; init; } = 1.0f;
    public string SourceType { get; init; } = "llm";
    public string? SourceRef { get; init; }
    public bool IsUncertain { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
