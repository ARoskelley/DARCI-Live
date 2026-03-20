#nullable enable

using Darci.Memory.Confidence;
using Darci.Memory.Graph;
using Darci.Memory.Graph.Models;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

public sealed class KnowledgeAssessor
{
    /// <summary>Graph confidence at or above this = skip agents entirely.</summary>
    public const float HighConfidenceThreshold = 0.65f;

    /// <summary>Graph confidence below this = run full agent suite.</summary>
    public const float LowConfidenceThreshold = 0.35f;

    /// <summary>
    /// Between LOW and HIGH = ambiguous range.
    /// LLM gap classifier fires to decide whether RunGapFill or SkipAgents.
    /// </summary>

    private readonly IKnowledgeGraph _graph;
    private readonly IConfidenceTracker _confidence;
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<KnowledgeAssessor> _logger;

    public KnowledgeAssessor(
        IKnowledgeGraph graph,
        IConfidenceTracker confidence,
        IResearchToolbox toolbox,
        ILogger<KnowledgeAssessor> logger)
    {
        _graph = graph;
        _confidence = confidence;
        _toolbox = toolbox;
        _logger = logger;
    }

    public async Task<KnowledgeAssessment> AssessAsync(
        string topic,
        CancellationToken ct = default)
    {
        // ── Step 1: Query knowledge graph ──
        var entities = await _graph.SearchEntitiesAsync(topic, limit: 5, ct: ct);

        // ── Step 2: Query confidence tracker ──
        List<float>? embedding = null;
        try { embedding = await _toolbox.GetEmbeddingAsync(topic, ct); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embedding failed for topic: {Topic}", topic);
        }

        var synthesis = await _confidence.SynthesizeAsync(
            topic,
            domain: null,
            getEmbedding: embedding is not null ? _ => Task.FromResult(embedding) : null,
            ct: ct);

        var graphConfidence = synthesis.AggregateConf;

        // ── Step 3: Apply threshold logic ──
        if (graphConfidence >= HighConfidenceThreshold)
        {
            _logger.LogDebug(
                "Knowledge assessment: SKIP AGENTS (confidence {Conf:P0}) for '{Topic}'",
                graphConfidence, topic);
            return new KnowledgeAssessment
            {
                Topic = topic,
                GraphConfidence = graphConfidence,
                SupportingClaims = synthesis.SupportingClaims,
                RelevantEntities = entities,
                Decision = DispatchDecision.SkipAgents,
                DecisionReason = $"Graph confidence {graphConfidence:P0} >= {HighConfidenceThreshold:P0} threshold",
            };
        }

        if (graphConfidence < LowConfidenceThreshold)
        {
            _logger.LogDebug(
                "Knowledge assessment: RUN AGENTS (confidence {Conf:P0}) for '{Topic}'",
                graphConfidence, topic);
            return new KnowledgeAssessment
            {
                Topic = topic,
                GraphConfidence = graphConfidence,
                SupportingClaims = synthesis.SupportingClaims,
                RelevantEntities = entities,
                Decision = DispatchDecision.RunAgents,
                DecisionReason = $"Graph confidence {graphConfidence:P0} < {LowConfidenceThreshold:P0} threshold",
            };
        }

        // ── Step 4: Ambiguous range — LLM gap classifier ──
        bool? llmClassified = null;
        try
        {
            var claimsSummary = string.Join("\n",
                synthesis.SupportingClaims.Take(5).Select(c => $"- {c.Statement} ({c.Confidence:P0})"));
            var classifierPrompt = $"""
You are a knowledge gap classifier. Respond with only one word: YES or NO.
Question: Does the following existing knowledge adequately answer the topic, or is
external research needed to give a complete, up-to-date answer?
Topic: {topic}
Existing knowledge (confidence {graphConfidence:P0}):
{claimsSummary}
Answer YES if external research is needed. Answer NO if existing knowledge is sufficient.
""";
            var classifierResponse = (await _toolbox.GenerateAsync(classifierPrompt, ct)).Trim();
            llmClassified = classifierResponse.StartsWith("YES", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LLM gap classifier failed; defaulting to RunGapFill");
        }

        var decision = llmClassified == false
            ? DispatchDecision.SkipAgents
            : DispatchDecision.RunGapFill;

        _logger.LogDebug(
            "Knowledge assessment: {Decision} (confidence {Conf:P0}, LLM={Llm}) for '{Topic}'",
            decision, graphConfidence, llmClassified, topic);

        return new KnowledgeAssessment
        {
            Topic = topic,
            GraphConfidence = graphConfidence,
            SupportingClaims = synthesis.SupportingClaims,
            RelevantEntities = entities,
            LlmClassifiedAsGap = llmClassified,
            Decision = decision,
            DecisionReason = $"Ambiguous ({graphConfidence:P0}); LLM classifier: {llmClassified}",
        };
    }
}
