#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Stakes classification of a hypothesis/problem. Sensitive = medical / engineering / other high-stakes
/// domains where a wrong "validated" answer can hurt someone. Gating differs by domain (§4a): sensitive
/// entries get a LOWER mid-tier cap and NEVER auto-promote — evidence meeting a bar only raises a proposal.
/// </summary>
public enum KnowledgeDomain
{
    General = 0,
    Sensitive = 1,
}

/// <summary>
/// Classifies a hypothesis into <see cref="KnowledgeDomain"/>. DELIBERATELY SIMPLE for now (Phase E,
/// sub-unit 1): an explicit tag wins; otherwise a keyword scan. This is a conservative first cut — it errs
/// toward <see cref="KnowledgeDomain.Sensitive"/> on any hit, because a false "general" is the dangerous
/// direction. Flagged for a Fable second opinion: high-stakes gating hangs off this, and a keyword scan is
/// brittle (misses paraphrase, no context) — a small classifier model or a curated ontology may be warranted.
/// </summary>
public static class DomainClassifier
{
    /// <summary>Substrings that mark a problem as high-stakes. Lower-cased; matched as substrings so stems
    /// like "prosthe" catch "prosthesis"/"prosthetic" and "cardio" catches "cardiovascular".</summary>
    private static readonly string[] SensitiveKeywords =
    {
        // medical / clinical
        "medical", "clinical", "patient", "diagnos", "therap", "treatment", "drug", "dose", "dosage",
        "pharma", "surg", "implant", "prosthe", "cardio", "cardiac", "neuro", "patholog", "disease",
        "biomed", "physiolog", "vaccine", "toxic", "anesthe", "myoelectric",
        // engineering / physical safety
        "structural", "load-bearing", "load bearing", "aerospace", "avionics", "voltage", "high-voltage",
        "actuator", "torque", "pressure vessel", "combustion", "fatigue", "safety-critical", "safety critical",
        "brake", "chassis", "flight control", "reactor", "hazard",
    };

    /// <summary>Classify from an optional explicit tag plus any free-text signals (question, intent, hypothesis).</summary>
    public static KnowledgeDomain Classify(string? explicitTag, params string?[] texts)
    {
        if (!string.IsNullOrWhiteSpace(explicitTag))
        {
            if (explicitTag.Trim().Equals("sensitive", System.StringComparison.OrdinalIgnoreCase))
                return KnowledgeDomain.Sensitive;
            if (explicitTag.Trim().Equals("general", System.StringComparison.OrdinalIgnoreCase))
                return KnowledgeDomain.General;
            // Unknown tag → fall through to keyword scan rather than trusting an arbitrary string.
        }

        var hay = string.Join(" ", texts.Where(t => !string.IsNullOrWhiteSpace(t)))
            .ToLowerInvariant();
        if (hay.Length == 0) return KnowledgeDomain.Sensitive;   // fail CLOSED: unclassifiable ⇒ stricter gating

        return SensitiveKeywords.Any(k => hay.Contains(k, System.StringComparison.Ordinal))
            ? KnowledgeDomain.Sensitive
            : KnowledgeDomain.General;
    }
}
