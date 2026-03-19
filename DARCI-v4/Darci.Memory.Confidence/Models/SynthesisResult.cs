#nullable enable

namespace Darci.Memory.Confidence.Models;

public sealed record SynthesisResult
{
    public string Question { get; init; } = "";
    public float AggregateConf { get; init; }
    public bool IsUncertain { get; init; }
    public IReadOnlyList<KnowledgeClaim> SupportingClaims { get; init; } = Array.Empty<KnowledgeClaim>();
    public IReadOnlyList<Contradiction> ActiveContradictions { get; init; } = Array.Empty<Contradiction>();
    public string UncertaintyReason { get; init; } = "";
}
