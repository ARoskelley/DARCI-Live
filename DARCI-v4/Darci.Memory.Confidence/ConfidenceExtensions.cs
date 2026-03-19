#nullable enable

using Darci.Memory.Confidence.Models;

namespace Darci.Memory.Confidence;

public static class ConfidenceExtensions
{
    public static bool IsLowConfidence(this KnowledgeClaim claim, float threshold = 0.4f)
        => claim.Confidence < threshold;

    public static string Describe(this KnowledgeClaim claim)
        => $"{claim.Statement} (confidence {claim.Confidence:P0})";
}
