#nullable enable

using System.Text;
using Darci.Nodes;

namespace Darci.Research.Agents.Models;

/// <summary>What kind of knowledge the requester needs — shapes how the compiler structures the answer.</summary>
public enum KnowledgeKind
{
    FactLookup,   // a direct fact / definition
    GapFill,      // fill a specific knowledge gap behind a failure
    HowTo,        // concrete steps / paths forward
    CaseStudies,  // examples / precedents
}

/// <summary>
/// Rigid input contract for the KG / deep-research node (Phase 2). The node treats this as the only
/// way in; nothing else about the caller leaks through.
/// </summary>
public sealed record KnowledgeRequest(
    string Question,
    string Intent = "",
    string? FailureContext = null,
    KnowledgeKind Kind = KnowledgeKind.GapFill);

/// <summary>An example / precedent the answer is grounded in.</summary>
public sealed record KnowledgeCaseStudy(string Summary, string? SourceRef = null);

/// <summary>
/// Rigid, STRUCTURED output contract (decision 4) — never prose. Designed to be consumed by models not
/// optimized for language (and future non-language models): each aspect of the answer is a typed field,
/// and unknowns are explicit rather than buried in text. This is what replaces the old "check your SDK"
/// synthesis blob.
/// </summary>
public sealed record KnowledgeResponse
{
    /// <summary>Whether the final review judged this to actually answer the request.</summary>
    public bool Answered { get; init; }

    /// <summary>Unified confidence (conservative blend of source + review). Gap-aware.</summary>
    public Confidence Confidence { get; init; } = Confidence.Unassessed;

    /// <summary>A single concise direct answer, when one exists.</summary>
    public string DirectAnswer { get; init; } = "";

    /// <summary>Atomic, deduped factual findings.</summary>
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();

    /// <summary>Concrete steps / paths forward (for HowTo / GapFill).</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    /// <summary>Examples / case studies the answer draws on.</summary>
    public IReadOnlyList<KnowledgeCaseStudy> Examples { get; init; } = Array.Empty<KnowledgeCaseStudy>();

    /// <summary>Sources / citations behind the findings.</summary>
    public IReadOnlyList<ResearchCitation> Citations { get; init; } = Array.Empty<ResearchCitation>();

    /// <summary>Explicit unknowns / what could NOT be answered — drives the caller's next decision.</summary>
    public IReadOnlyList<string> Gaps { get; init; } = Array.Empty<string>();

    public static KnowledgeResponse Unanswered(string gap) =>
        new() { Answered = false, Confidence = Confidence.Unassessed, Gaps = new[] { gap } };

    /// <summary>Compact rendering of the structured content for review prompts and the legacy findings slot.</summary>
    public string ToReviewText()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(DirectAnswer)) sb.AppendLine($"Answer: {DirectAnswer}");
        if (Findings.Count > 0) { sb.AppendLine("Findings:"); foreach (var f in Findings) sb.AppendLine($"- {f}"); }
        if (Steps.Count > 0) { sb.AppendLine("Steps:"); foreach (var s in Steps) sb.AppendLine($"- {s}"); }
        if (Examples.Count > 0) { sb.AppendLine("Examples:"); foreach (var e in Examples) sb.AppendLine($"- {e.Summary}"); }
        if (Gaps.Count > 0) { sb.AppendLine("Gaps/Unknowns:"); foreach (var g in Gaps) sb.AppendLine($"- {g}"); }
        return sb.ToString().Trim();
    }
}

/// <summary>A reviewer's verdict on whether a candidate answer fulfills a request.</summary>
public sealed record KnowledgeReview(
    bool Fulfills,
    Confidence Confidence,
    IReadOnlyList<string> MissingAspects,
    string Reasoning);
