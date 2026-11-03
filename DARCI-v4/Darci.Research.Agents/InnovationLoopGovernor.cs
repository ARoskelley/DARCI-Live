#nullable enable

using Darci.Nodes;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>Runs the bounded, progress-driven, diverse-candidate innovation loop and returns the winner
/// (or an honest Unsolvable). Replaces the Phase B single-pass call inside the innovation node.</summary>
public interface IInnovationLoop
{
    Task<InnovationProposal> RunAsync(InnovationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Phase D loop governor (Fable Q2 shape). Each cycle: generate N DIVERSE candidates (each prompted to
/// differ) → screen them comparatively (one cheap call) → falsify the top 1–2 (separate reviewer). Stop on
/// accept, plateau (no improvement over K cycles), novelty-collapse (this cycle's diverse candidates came
/// back near-identical → substrate exhausted → Unsolvable), or the HARD budget backstop. N is adaptive
/// (drops to <see cref="InnovationLoopOptions.MinCandidates"/> under budget pressure) so it degrades
/// gracefully on constrained local hardware. A tiny cross-cycle archive (QD-lite) seeds the next cycle and
/// retracted ideas condition the generator against reproposing failures. Generator ≠ critic throughout.
/// </summary>
public sealed class InnovationLoopGovernor : IInnovationLoop
{
    private readonly IInnovationSynthesizer _generator;
    private readonly IInnovationCritic _critic;
    private readonly IKnowledgeReviewAgent _reviewer;   // falsification — separate from the generator
    private readonly IResearchToolbox _toolbox;         // embeddings (cheap; NOT counted against the call budget)
    private readonly IInnovatedKnowledgeStore _store;   // retracted negatives
    private readonly InnovationLoopOptions _options;
    private readonly ILogger<InnovationLoopGovernor> _logger;

    public InnovationLoopGovernor(
        IInnovationSynthesizer generator,
        IInnovationCritic critic,
        IKnowledgeReviewAgent reviewer,
        IResearchToolbox toolbox,
        IInnovatedKnowledgeStore store,
        InnovationLoopOptions options,
        ILogger<InnovationLoopGovernor> logger)
    {
        _generator = generator;
        _critic = critic;
        _reviewer = reviewer;
        _toolbox = toolbox;
        _store = store;
        _options = options;
        _logger = logger;
    }

    private sealed record Scored(InnovationProposal Candidate, double Quality, KnowledgeReview? Verdict, float[] Embedding);

    public async Task<InnovationProposal> RunAsync(InnovationRequest request, CancellationToken ct = default)
    {
        var opt = _options;
        var budget = opt.Budget;
        var deadline = DateTime.UtcNow.AddSeconds(budget.MaxWallClockSeconds);
        var calls = 0;

        var archive = new List<Scored>();
        var failed = await LoadRetractedNegativesAsync(ct);

        double bestQuality = 0;
        Scored? winner = null;
        var plateau = 0;

        for (var cycle = 1; cycle <= budget.MaxCycles; cycle++)
        {
            if (DateTime.UtcNow >= deadline || calls >= budget.MaxGenerativeCalls)
            {
                _logger.LogInformation("Innovation loop hit the budget backstop (cycle {Cycle}, {Calls} calls).", cycle, calls);
                break;
            }

            var n = AdaptiveN(calls, budget, opt);

            // ── Generate N diverse candidates (each told to differ from the rest + the archive) ──
            var candidates = new List<InnovationProposal>();
            for (var i = 0; i < n && calls < budget.MaxGenerativeCalls; i++)
            {
                var avoid = candidates.Select(c => c.Hypothesis)
                    .Concat(archive.Select(a => a.Candidate.Hypothesis))
                    .Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
                var cand = await _generator.GenerateCandidateAsync(request, avoid, failed, ct);
                calls++;
                if (cand.Status != ProposalStatus.Unsolvable && !string.IsNullOrWhiteSpace(cand.Hypothesis))
                    candidates.Add(cand);
            }

            if (candidates.Count == 0)
            {
                if (cycle == 1)
                    return Unsolvable("The generator produced no usable candidate from the known material.");
                if (++plateau >= opt.PlateauCycles) break;
                continue;
            }

            // ── Embeddings (for novelty + archive) ──
            var embeddings = new List<float[]>(candidates.Count);
            foreach (var c in candidates) embeddings.Add(await EmbedAsync(c.Hypothesis, ct));

            // ── Novelty collapse: diverse-prompted candidates came back near-identical → substrate exhausted ──
            if (candidates.Count >= 2)
            {
                var spread = MeanPairwiseDistance(embeddings);
                if (spread >= 0 && spread < opt.NoveltyCollapseThreshold)
                {
                    _logger.LogInformation("Innovation loop novelty collapse (spread {Spread:0.###} < {Thr}); concluding unsolvable.",
                        spread, opt.NoveltyCollapseThreshold);
                    return Unsolvable(
                        "Candidate diversity collapsed — the known substrate is exhausted for this problem.",
                        new[] { "new external data or a distinct knowledge domain" });
                }
            }

            // ── Screen (one comparative call) then falsify only the top survivors ──
            IReadOnlyList<int> ranked = candidates.Count >= 2
                ? await ScreenCountedAsync(request, candidates, () => calls++, ct)
                : new[] { 0 };

            var scored = new List<Scored>();
            foreach (var idx in ranked.Take(opt.SurvivorsToFalsify))
            {
                if (calls >= budget.MaxGenerativeCalls) break;
                var cand = candidates[idx];
                var verdict = await _reviewer.ReviewAsync(
                    new KnowledgeRequest(request.Question, request.Intent, request.FailureContext, KnowledgeKind.HowTo),
                    CandidateText(cand), "innovation-candidate", ct);
                calls++;
                var quality = verdict.Confidence.IsAssessed ? verdict.Confidence.Score : (verdict.Fulfills ? 0.5 : 0.25);
                scored.Add(new Scored(cand, quality, verdict, embeddings[idx]));
            }

            if (scored.Count == 0) break;   // budget ran out mid-cycle

            var cycleBest = scored.OrderByDescending(s => s.Quality).First();

            var novelVsArchive = !IsNearArchive(cycleBest.Embedding, archive, opt.ArchiveClusterThreshold);
            var improved = cycleBest.Quality > bestQuality + 1e-6;
            if (improved) { bestQuality = cycleBest.Quality; winner = cycleBest; }

            foreach (var s in scored) ArchiveAdd(archive, s, opt);

            if (bestQuality >= opt.AcceptThreshold)
            {
                _logger.LogInformation("Innovation loop accepted a candidate (quality {Q:0.##}) at cycle {Cycle}.", bestQuality, cycle);
                break;
            }

            if (improved || novelVsArchive) plateau = 0;
            else if (++plateau >= opt.PlateauCycles)
            {
                _logger.LogInformation("Innovation loop plateaued (no improvement over {K} cycles).", opt.PlateauCycles);
                break;
            }
        }

        if (winner is null)
            return Unsolvable("No viable candidate survived review within budget.",
                new[] { "new external data or a distinct knowledge domain" });

        // Winner → capped Innovated proposal. Critic quality lives in Plausibility; stored confidence is
        // always IsLow (a hypothesis, never a fact); a better candidate stores slightly higher WITHIN the cap.
        var storedConfidence = ProvenancePolicy.Clamp(Provenance.Innovated, Confidence.Of(0.2 + 0.15 * winner.Quality));
        return winner.Candidate with
        {
            Status = ProposalStatus.VettedInternally,
            Plausibility = winner.Verdict,
            Provenance = Provenance.Innovated,
            Confidence = storedConfidence,
        };
    }

    private async Task<IReadOnlyList<int>> ScreenCountedAsync(InnovationRequest req, IReadOnlyList<InnovationProposal> cands, Action countCall, CancellationToken ct)
    {
        var ranked = await _critic.ScreenAsync(req, cands, ct);
        countCall();
        return ranked;
    }

    private async Task<List<string>> LoadRetractedNegativesAsync(CancellationToken ct)
    {
        try
        {
            var retracted = await _store.GetByProvenanceAsync(Provenance.Retracted, _options.RetractedNegativesLimit, ct);
            return retracted.Select(r => r.Hypothesis).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Loading retracted negatives failed (non-fatal).");
            return new();
        }
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        try { return (await _toolbox.GetEmbeddingAsync(text, ct)).ToArray(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Embedding failed (non-fatal).");
            return Array.Empty<float>();
        }
    }

    /// <summary>N per cycle, dropping to the floor under budget pressure and never exceeding what the
    /// remaining call budget can afford (leaving room for the screen + falsification calls).</summary>
    internal static int AdaptiveN(int calls, InnovationBudget budget, InnovationLoopOptions opt)
    {
        var pressured = calls >= opt.BudgetPressureFraction * budget.MaxGenerativeCalls;
        var n = pressured ? opt.MinCandidates : opt.CandidatesPerCycle;
        var remaining = budget.MaxGenerativeCalls - calls;
        var affordableGen = Math.Max(1, remaining - (1 + opt.SurvivorsToFalsify)); // reserve screen + falsify
        return Math.Clamp(Math.Min(n, affordableGen), 1, opt.CandidatesPerCycle);
    }

    // ── archive (QD-lite: best-per-embedding-cluster, capped) ──

    private static void ArchiveAdd(List<Scored> archive, Scored s, InnovationLoopOptions opt)
    {
        for (var i = 0; i < archive.Count; i++)
        {
            if (Distance(archive[i].Embedding, s.Embedding) < opt.ArchiveClusterThreshold)
            {
                if (s.Quality > archive[i].Quality) archive[i] = s;   // keep the best of the cluster
                return;
            }
        }
        archive.Add(s);
        if (archive.Count > opt.ArchiveCap)
        {
            var worst = archive.Select((x, i) => (x, i)).OrderBy(t => t.x.Quality).First().i;
            archive.RemoveAt(worst);
        }
    }

    private static bool IsNearArchive(float[] emb, List<Scored> archive, double threshold)
        => archive.Any(a => Distance(a.Embedding, emb) < threshold);

    // ── vector helpers ──

    private static double MeanPairwiseDistance(IReadOnlyList<float[]> embs)
    {
        if (embs.Count < 2 || embs.Any(e => e.Length == 0)) return -1; // unknown → don't trigger collapse
        double sum = 0; var pairs = 0;
        for (var i = 0; i < embs.Count; i++)
            for (var j = i + 1; j < embs.Count; j++) { sum += Distance(embs[i], embs[j]); pairs++; }
        return pairs == 0 ? -1 : sum / pairs;
    }

    /// <summary>Distance in [0,1]: 0 = identical direction, 1 = orthogonal/opposite. Empty vectors → 1 (treated as different).</summary>
    private static double Distance(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 1.0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 1.0;
        var cos = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        return 1.0 - Math.Max(0.0, Math.Min(1.0, cos));
    }

    private static string CandidateText(InnovationProposal p)
    {
        var reasoning = p.Reasoning.Count > 0 ? "\nReasoning: " + string.Join("; ", p.Reasoning.Select(r => r.Inference)) : "";
        return p.Hypothesis + reasoning;
    }

    private static InnovationProposal Unsolvable(string reason, IReadOnlyList<string>? required = null)
        => InnovationProposal.CannotSolve(reason, required ?? new[] { "a clearer problem statement or additional data" });
}
