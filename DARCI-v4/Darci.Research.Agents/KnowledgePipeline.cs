#nullable enable

using Darci.Nodes;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>Tunable knobs for the KG/DR pipeline (decision 4: explicit/configurable thresholds).</summary>
public sealed class KnowledgePipelineOptions
{
    /// <summary>
    /// Number of cheap (knowledge-graph) review failures tolerated before escalating to deep research.
    /// With a single KG source the effective behaviour is "escalate when the KG answer fails review".
    /// </summary>
    public int EscalateAfterReviewFailures { get; init; } = 1;
}

/// <summary>The rigid KG/DR pipeline as a black box: a request in, a structured response out.</summary>
public interface IKnowledgePipeline
{
    Task<KnowledgeResponse> RunAsync(KnowledgeRequest request, CancellationToken ct = default);
}

/// <summary>
/// Implements Tinman's spec end to end:
///   request → admin/KG consult → review #1 (does KG suffice?) → escalate to deep research after N
///   failures → compiler (structure + cut fluff) → review #2 (does it actually answer?) → return.
/// Reuses the existing <see cref="IKnowledgeAssessor"/> (admin/KG) and <see cref="IDeepResearchOrchestrator"/>
/// (agent fan-out) for the ~70% that already existed, and adds the review + compiler stages.
/// </summary>
public sealed class KnowledgePipeline : IKnowledgePipeline
{
    private readonly IKnowledgeAssessor _assessor;
    private readonly IKnowledgeReviewAgent _review;
    private readonly IKnowledgeCompilerAgent _compiler;
    private readonly IDeepResearchOrchestrator _research;
    private readonly KnowledgePipelineOptions _options;
    private readonly ILogger<KnowledgePipeline> _logger;

    public KnowledgePipeline(
        IKnowledgeAssessor assessor,
        IKnowledgeReviewAgent review,
        IKnowledgeCompilerAgent compiler,
        IDeepResearchOrchestrator research,
        KnowledgePipelineOptions options,
        ILogger<KnowledgePipeline> logger)
    {
        _assessor = assessor;
        _review = review;
        _compiler = compiler;
        _research = research;
        _options = options;
        _logger = logger;
    }

    public async Task<KnowledgeResponse> RunAsync(KnowledgeRequest request, CancellationToken ct = default)
    {
        var fullQuestion = string.IsNullOrWhiteSpace(request.FailureContext)
            ? request.Question
            : $"{request.Question}\n\nObserved failure context:\n{request.FailureContext}";

        // ── Stage 1: admin / KG consult ──
        string answerText = "";
        var citations = (IReadOnlyList<ResearchCitation>)Array.Empty<ResearchCitation>();
        var sourceConfidence = Confidence.Unassessed;
        var source = "knowledge-graph";
        var accepted = false;
        var reviewFailures = 0;

        KnowledgeAssessment? assessment = null;
        try { assessment = await _assessor.AssessAsync(request.Question, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "KG assessment failed; will escalate to deep research.");
        }

        var kgText = assessment is { SupportingClaims.Count: > 0 }
            ? string.Join("\n", assessment.SupportingClaims.Take(8).Select(c => c.Statement))
            : "";

        if (!string.IsNullOrWhiteSpace(kgText))
        {
            // ── Stage 2: review #1 — does the KG answer fulfill the request? ──
            var r1 = await _review.ReviewAsync(request, kgText, "knowledge-graph", ct);
            if (r1.Fulfills)
            {
                answerText = kgText;
                sourceConfidence = MostConservative(assessment!.Confidence, r1.Confidence);
                accepted = true;
                _logger.LogInformation("KG answer accepted by review; skipping deep research.");
            }
            else
            {
                reviewFailures++;
                _logger.LogInformation("KG answer rejected by review: {Reason}", r1.Reasoning);
            }
        }

        // ── Stage 3: escalate to deep research (no usable KG, or enough review failures) ──
        if (!accepted)
        {
            var mustEscalate = string.IsNullOrWhiteSpace(kgText)
                               || reviewFailures >= _options.EscalateAfterReviewFailures;
            if (mustEscalate)
            {
                _logger.LogInformation("Escalating to deep research (kgEmpty={Empty}, failures={Failures}).",
                    string.IsNullOrWhiteSpace(kgText), reviewFailures);
                var outcome = await _research.RunDeepResearchAsync(fullQuestion, "DARCI", ct);
                answerText = outcome.FinalAnswer;
                citations = outcome.Citations;
                sourceConfidence = outcome.Confidence;
                source = "deep-research";
            }
            else
            {
                // Below escalation threshold but no accepted answer — proceed with the KG text and let
                // review #2 flag the gaps rather than spending on research.
                answerText = kgText;
                source = "knowledge-graph (below escalation threshold)";
                sourceConfidence = assessment?.Confidence ?? Confidence.Unassessed;
            }
        }

        if (string.IsNullOrWhiteSpace(answerText))
            return KnowledgeResponse.Unanswered("No knowledge could be gathered for this request.");

        // ── Stage 4: compiler — structure + cut fluff ──
        var compiled = await _compiler.CompileAsync(request, answerText, citations, ct);

        // ── Stage 5: review #2 — does the structured answer actually answer the request? ──
        var r2 = await _review.ReviewAsync(request, compiled.ToReviewText(), source, ct);
        var gaps = compiled.Gaps.ToList();
        if (!r2.Fulfills) gaps.AddRange(r2.MissingAspects);

        return compiled with
        {
            Answered = r2.Fulfills,
            Confidence = MostConservative(sourceConfidence, r2.Confidence),
            Gaps = gaps.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Citations = citations.Count > 0 ? citations : compiled.Citations,
        };
    }

    /// <summary>Conservative confidence blend: the lower of the two assessed scores, preserving gap
    /// detection (if either source or review is low/unassessed, the result is not over-confident).</summary>
    internal static Confidence MostConservative(Confidence a, Confidence b)
    {
        if (!a.IsAssessed) return b;
        if (!b.IsAssessed) return a;
        return a.Score <= b.Score ? a : b;
    }
}
