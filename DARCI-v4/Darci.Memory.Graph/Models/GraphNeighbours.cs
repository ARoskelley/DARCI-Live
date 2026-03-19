#nullable enable

namespace Darci.Memory.Graph.Models;

public sealed record GraphNeighbours
{
    public string RootEntityId { get; init; } = "";
    public int Depth { get; init; }
    public IReadOnlyList<KgEntity> Entities { get; init; } = Array.Empty<KgEntity>();
    public IReadOnlyList<KgRelation> Relations { get; init; } = Array.Empty<KgRelation>();
}
