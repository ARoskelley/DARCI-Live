#nullable enable

namespace Darci.Memory.Graph.Models;

public sealed record KgRelation
{
    public string Id { get; init; } = "";
    public string FromEntityId { get; init; } = "";
    public string ToEntityId { get; init; } = "";
    public string RelationType { get; init; } = "";
    public string Direction { get; init; } = "directed";
    public float Weight { get; init; } = 1.0f;
    public float Confidence { get; init; } = 0.5f;
    public string[] EvidenceIds { get; init; } = Array.Empty<string>();
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
