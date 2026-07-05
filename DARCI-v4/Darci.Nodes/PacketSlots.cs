#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Well-known payload slot keys (decision 4: structured, parseable contract — usable by non-language
/// models). Nodes read and tack onto these by convention rather than ad-hoc string keys.
/// </summary>
public static class PacketSlots
{
    // Coding node
    public const string CodingTaskId = "codingTaskId";
    public const string WorkspaceId = "workspaceId";

    // Knowledge node — request
    public const string Question = "question";
    public const string FailureContext = "failureContext";

    // Gap handling — whether the requesting work is blocked on this (critical path) and how many
    // immediate-fill hops deep we are (recursion guard).
    public const string Blocking = "blocking";
    public const string GapFillDepth = "gapFillDepth";

    // Knowledge node — request
    public const string KnowledgeKind = "knowledgeKind";

    // Knowledge node — response
    public const string KnowledgeFindings = "knowledgeFindings";          // human/agent-readable rendering (legacy/compat)
    public const string KnowledgeConfidence = "knowledgeConfidence";
    public const string KnowledgeResponse = "knowledgeResponse";          // structured KnowledgeResponse as JSON (decision 4)

    // Innovation node — request (JSON arrays) and response
    public const string InnovationGaps = "innovationGaps";                // JSON string[] — the unmet gaps that triggered escalation
    public const string InnovationKnownFacts = "innovationKnownFacts";    // JSON string[] — KG/DR substrate to recombine
    public const string InnovationProposal = "innovationProposal";        // structured InnovationProposal as JSON
}
