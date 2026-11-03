#nullable enable

using Darci.Memory.Confidence.Models;
using Darci.Memory.Graph.Models;
using Darci.Nodes;

namespace Darci.Research.Agents.Models;

/// <summary>
/// Output of Phase 1 (Knowledge Assessment).
/// Summarises what DARCI already knows before any agents fire.
/// </summary>
public sealed record KnowledgeAssessment
{
    /// <summary>The question or topic being assessed.</summary>
    public string Topic { get; init; } = "";

    /// <summary>
    /// Aggregate confidence across all relevant graph claims (unified <see cref="Darci.Nodes.Confidence"/>).
    /// 0.0 = no knowledge. 1.0 = complete, highly corroborated knowledge. Unassessed = no claims found.
    /// </summary>
    public Confidence Confidence { get; init; } = Confidence.Unassessed;

    /// <summary>Top relevant claims from the confidence tracker.</summary>
    public IReadOnlyList<KnowledgeClaim> SupportingClaims { get; init; }
        = Array.Empty<KnowledgeClaim>();

    /// <summary>Entities found in the knowledge graph for this topic.</summary>
    public IReadOnlyList<KgEntity> RelevantEntities { get; init; }
        = Array.Empty<KgEntity>();

    /// <summary>
    /// Whether the LLM gap classifier determined external knowledge is needed.
    /// Only populated when GraphConfidence is in the ambiguous range (0.35–0.65).
    /// </summary>
    public bool? LlmClassifiedAsGap { get; init; }

    /// <summary>
    /// Dispatch decision made by KnowledgeAssessor.
    /// SkipAgents = graph knowledge sufficient.
    /// RunAgents  = agents needed.
    /// RunGapFill = partial knowledge, run targeted follow-up only.
    /// </summary>
    public DispatchDecision Decision { get; init; }

    /// <summary>Human-readable reason for the dispatch decision (for logging).</summary>
    public string DecisionReason { get; init; } = "";
}

public enum DispatchDecision
{
    SkipAgents,   // confidence >= HIGH_THRESHOLD — use graph knowledge directly
    RunGapFill,   // confidence in ambiguous range — run targeted gap-fill only
    RunAgents,    // confidence low or empty — run full agent suite
}
