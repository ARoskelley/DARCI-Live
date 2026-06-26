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

    // Knowledge node — response
    public const string KnowledgeFindings = "knowledgeFindings";
    public const string KnowledgeConfidence = "knowledgeConfidence";
}
