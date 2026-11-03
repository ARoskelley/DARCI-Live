#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>Tunables for automatic outcome processing (all downward or within-cap only — never promotes).</summary>
public sealed class OutcomeFeedbackOptions
{
    /// <summary>Distinct-root failures at/above which a failing entry is fully Retracted instead of demoted one stage.</summary>
    public int SevereFailureRetractThreshold { get; init; } = 3;
}

/// <summary>
/// Applies coding/environment outcomes to innovated entries. Per the governing invariant (§0a) this is
/// STRICTLY non-promoting:
///   success → append SUCCESS evidence (deduped by distinct correlation root), nudge the WITHIN-CAP score
///             for ranking only — Provenance is left UNTOUCHED (trust never rises automatically);
///   failure → append FAILURE evidence + DEMOTE ONE STAGE (full Retract only from the bottom stage or on
///             repeated/severe failure); notify the UI node if the entry had been human-promoted.
/// Outcomes are matched via CONSUMPTION LINKS and counted by distinct correlation root, so retries of the
/// same task collapse to one and independent uses count separately (the correlation-link fix).
/// </summary>
public sealed class InnovatedKnowledgeOutcomeSink : IOutcomeFeedbackSink
{
    private readonly IInnovatedKnowledgeStore _store;
    private readonly OutcomeFeedbackOptions _options;
    private readonly ILogger<InnovatedKnowledgeOutcomeSink> _logger;
    private readonly IProvenanceDemotionNotifier? _demotionNotifier;

    public InnovatedKnowledgeOutcomeSink(
        IInnovatedKnowledgeStore store,
        OutcomeFeedbackOptions options,
        ILogger<InnovatedKnowledgeOutcomeSink> logger,
        IProvenanceDemotionNotifier? demotionNotifier = null)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _demotionNotifier = demotionNotifier;
    }

    public async Task ApplyAsync(OutcomeFeedback feedback, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedback.CorrelationId)) return;

        var outcome = feedback.Success ? ConsumptionOutcome.Success : ConsumptionOutcome.Failure;

        // Match by consumption link, not by the entry's originating correlation.
        var entries = await _store.GetEntriesByConsumptionRootAsync(feedback.CorrelationId, ct);
        foreach (var entry in entries)
        {
            // Dedupe: a retry under the same root that's already resolved changes nothing (collapse to one).
            var newlyResolved = await _store.ResolveConsumptionAsync(entry.Id, feedback.CorrelationId, outcome, ct);
            if (!newlyResolved) continue;

            var (successes, failures) = await _store.CountDistinctOutcomesAsync(entry.Id, ct);

            if (feedback.Success)
                await ApplySuccessAsync(entry, successes, failures, feedback, ct);
            else
                await ApplyFailureAsync(entry, successes, failures, feedback, ct);
        }
    }

    private async Task ApplySuccessAsync(InnovatedKnowledgeRecord e, int successes, int failures, OutcomeFeedback fb, CancellationToken ct)
    {
        // Provenance UNTOUCHED. Only append evidence + a within-cap ranking nudge (still IsLow, not trust).
        var rankingScore = Math.Min(ProvenancePolicy.InnovatedCap, 0.1 * (successes + 1));
        var updated = e with
        {
            SuccessCount = successes,
            FailureCount = failures,
            Confidence = Confidence.Of(rankingScore, e.Confidence.Note),
        };
        await _store.UpdateAsync(updated,
            new LedgerEvent(LedgerEventKind.SuccessEvidence,
                $"success evidence → {successes} distinct success(es); ranking {rankingScore:0.##}",
                CorrelationRoot: fb.CorrelationId, Note: fb.Evidence),
            ct);
        _logger.LogInformation("Innovated {Id}: success evidence appended ({N} distinct), provenance unchanged.", e.Id, successes);
    }

    private async Task ApplyFailureAsync(InnovatedKnowledgeRecord e, int successes, int failures, OutcomeFeedback fb, CancellationToken ct)
    {
        var wasHumanPromoted = e.Provenance is Provenance.UnderTest or Provenance.ProvisionallyValidated or Provenance.HumanApproved;
        var severe = failures >= _options.SevereFailureRetractThreshold;

        // Soft: one stage down. Bottom stage (Innovated/Unverified) demotes to Retracted; severe → straight Retracted.
        var newProvenance = severe ? Provenance.Retracted : ProvenancePolicy.DemoteOneStage(e.Provenance);

        var updated = e with
        {
            SuccessCount = successes,
            FailureCount = failures,
            Provenance = newProvenance,
            Confidence = ProvenancePolicy.Clamp(newProvenance, e.Confidence),
        };

        var change = newProvenance == Provenance.Retracted
            ? $"failure evidence → Retracted ({failures} distinct failure(s){(severe ? ", severe" : ", bottom stage")})"
            : $"failure evidence → demoted {e.Provenance} → {newProvenance}";

        await _store.UpdateAsync(updated,
            new LedgerEvent(LedgerEventKind.FailureEvidence, change, CorrelationRoot: fb.CorrelationId, Note: fb.Evidence),
            ct);
        _logger.LogInformation("Innovated {Id}: {Change}.", e.Id, change);

        if (wasHumanPromoted && _demotionNotifier is not null)
        {
            try { await _demotionNotifier.NotifyAsync(e, updated, fb, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Demotion notification failed for {Id} (non-fatal).", e.Id);
            }
        }
    }
}
