#nullable enable

namespace Darci.Memory.Graph.Models;

public sealed record KgEntity
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string Domain { get; init; } = "general";
    public string Description { get; init; } = "";
    public string[] Aliases { get; init; } = Array.Empty<string>();
    public float[]? Embedding { get; init; }
    public float Confidence { get; init; } = 0.5f;
    public int SourceCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
