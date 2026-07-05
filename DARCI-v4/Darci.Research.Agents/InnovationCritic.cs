#nullable enable

using System.Text;
using System.Text.Json;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// The cheap SCREEN half of the screen-then-falsify critic (Phase D cost control): ONE batched call that
/// ranks the cycle's candidates best→worst. Comparative ranking is cheaper and more reliable than absolute
/// scoring; only the top survivors get the full falsification review (IKnowledgeReviewAgent). Separate
/// agent from the generator (generator ≠ critic).
/// </summary>
public interface IInnovationCritic
{
    /// <summary>Returns candidate indices ranked best→worst. Falls back to input order on failure.</summary>
    Task<IReadOnlyList<int>> ScreenAsync(InnovationRequest request, IReadOnlyList<InnovationProposal> candidates, CancellationToken ct = default);
}

public sealed class OllamaInnovationCritic : IInnovationCritic
{
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<OllamaInnovationCritic> _logger;

    public OllamaInnovationCritic(IResearchToolbox toolbox, ILogger<OllamaInnovationCritic> logger)
    {
        _toolbox = toolbox;
        _logger = logger;
    }

    public async Task<IReadOnlyList<int>> ScreenAsync(InnovationRequest request, IReadOnlyList<InnovationProposal> candidates, CancellationToken ct = default)
    {
        var identity = Enumerable.Range(0, candidates.Count).ToList();
        if (candidates.Count <= 1) return identity;

        string raw;
        try { raw = await _toolbox.GenerateAsync(BuildPrompt(request, candidates), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Innovation screen unavailable; keeping input order.");
            return identity;
        }

        return ParseRanking(raw, candidates.Count) ?? identity;
    }

    private static string BuildPrompt(InnovationRequest req, IReadOnlyList<InnovationProposal> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a neutral technical reviewer. Rank the candidate solutions below from BEST to");
        sb.AppendLine("WORST for actually solving the problem — judge the ideas on merit only. Do not favour any");
        sb.AppendLine("for length or confidence of wording.");
        sb.AppendLine("Respond with ONLY a JSON array of candidate indices, best first, e.g. [2,0,1].");
        sb.AppendLine();
        sb.AppendLine($"PROBLEM: {req.Question}");
        sb.AppendLine("CANDIDATES:");
        for (var i = 0; i < candidates.Count; i++)
            sb.AppendLine($"[{i}] {candidates[i].Hypothesis}");
        sb.AppendLine("JSON:");
        return sb.ToString();
    }

    internal static IReadOnlyList<int>? ParseRanking(string raw, int count)
    {
        var start = raw.IndexOf('[');
        var end = raw.IndexOf(']', start + 1);
        if (start < 0 || end < 0) return null;
        try
        {
            var arr = JsonSerializer.Deserialize<int[]>(raw.Substring(start, end - start + 1));
            if (arr is null) return null;
            var seen = new HashSet<int>();
            var ranked = arr.Where(i => i >= 0 && i < count && seen.Add(i)).ToList();
            // Append any indices the model omitted so the ranking is a full permutation.
            foreach (var i in Enumerable.Range(0, count)) if (seen.Add(i)) ranked.Add(i);
            return ranked.Count == count ? ranked : null;
        }
        catch { return null; }
    }
}
