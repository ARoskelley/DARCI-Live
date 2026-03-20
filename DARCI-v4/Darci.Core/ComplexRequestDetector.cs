namespace Darci.Core;

/// <summary>
/// Deterministic (no LLM) detector for complex multi-domain requests that should
/// be routed to EngineeringCollection rather than single-part CAD or generic Task.
///
/// Runs in &lt;1ms before the LLM classification tier fires.
/// A request is complex if it contains a known complexity signal phrase, OR if it
/// touches at least two distinct domain clusters simultaneously.
/// </summary>
public static class ComplexRequestDetector
{
    private static readonly string[][] DomainClusters =
    {
        new[] { "prosthetic", "orthotic", "exoskeleton", "brace", "limb", "wearable" },
        new[] { "biomechanical", "ergonomic", "spine", "load", "torque", "weight distribution" },
        new[] { "emg", "electromyography", "muscle", "nerve", "signal", "sensor" },
        new[] { "motor", "actuator", "servo", "joint", "degree of freedom", "kinematics" },
        new[] { "harness", "mount", "attachment", "strap", "bracket", "frame" },
        new[] { "design", "engineer", "build", "fabricate", "manufacture", "3d print" },
        new[] { "research", "study", "literature", "evidence", "clinical", "specification" },
    };

    private static readonly string[] ComplexitySignals =
    {
        "third arm", "extra arm", "additional limb", "back mounted", "dorsal mount",
        "full assembly", "multi-part", "complete system", "integrated",
        "with harness", "with straps", "wearable", "body mounted",
    };

    /// <summary>
    /// Returns true if the message appears to be a complex multi-domain engineering request.
    /// </summary>
    public static bool IsComplex(string message)
    {
        var lower = message.ToLowerInvariant();

        if (ComplexitySignals.Any(s => lower.Contains(s)))
            return true;

        int clusterMatches = DomainClusters.Count(cluster =>
            cluster.Any(term => lower.Contains(term)));

        return clusterMatches >= 2;
    }
}
