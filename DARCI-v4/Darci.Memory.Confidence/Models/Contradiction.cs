#nullable enable

namespace Darci.Memory.Confidence.Models;

public sealed record Contradiction
{
    public string Id { get; init; } = "";
    public string ClaimAId { get; init; } = "";
    public string ClaimBId { get; init; } = "";
    public float Severity { get; init; } = 0.5f;
    public bool Resolved { get; init; }
    public string? Resolution { get; init; }
    public DateTime CreatedAt { get; init; }
}
