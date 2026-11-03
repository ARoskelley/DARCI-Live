#nullable enable

using Darci.Research.Agents.Models;

namespace Darci.Research.Agents;

/// <summary>
/// The "admin agent" of the KG/DR pipeline: consults the knowledge graph + confidence tracker to
/// produce an initial assessment of what DARCI already knows about a topic. Extracted as an interface
/// so the pipeline can be tested without the full graph/confidence stack.
/// </summary>
public interface IKnowledgeAssessor
{
    Task<KnowledgeAssessment> AssessAsync(string topic, CancellationToken ct = default);
}
