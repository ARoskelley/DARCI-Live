#nullable enable

namespace Darci.Memory.Graph.Models;

public sealed record GraphPath
{
    public IReadOnlyList<GraphPathStep> Steps { get; init; } = Array.Empty<GraphPathStep>();

    public bool IsEmpty => Steps.Count == 0;

    public int HopCount => Math.Max(0, Steps.Count - 1);

    public static GraphPath Empty { get; } = new();
}

public sealed record GraphPathStep
{
    public KgEntity Entity { get; init; } = new();
    public KgRelation? ViaRelation { get; init; }
}
