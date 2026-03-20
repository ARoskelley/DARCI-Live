#nullable enable

namespace Darci.Research.Agents;

/// <summary>
/// Controls what the orchestrator does after agents complete.
/// The caller sets this based on context — not the orchestrator itself.
/// </summary>
public enum ResearchOrchestrationMode
{
    /// <summary>
    /// Learn only. Agents run (if needed), results ingested into graph.
    /// No synthesis LLM call. Used for autonomous gap-filling during goal work.
    /// </summary>
    LearnOnly,

    /// <summary>
    /// Learn and synthesize. Agents run (if needed), results ingested,
    /// then Ollama produces a reply. Used for message-triggered research.
    /// </summary>
    LearnAndSynthesize,
}
