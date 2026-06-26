#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace Darci.Coding;

/// <summary>How a build/verification failure should be handled when deciding whether to escalate.</summary>
public enum BuildFailureClass
{
    /// <summary>Duplicate type/member (CS0101/CS0111) — the agent re-emitted a file it already created.
    /// Self-inflicted; research won't help. Inject targeted guidance instead.</summary>
    SelfInflictedDuplicate,

    /// <summary>A genuine failure with no self-inflicted signature — a candidate for deep research.</summary>
    KnowledgeGap,
}

/// <summary>Result of classifying a build/verification failure.</summary>
public sealed record BuildFailureClassification(
    BuildFailureClass Class,
    IReadOnlyList<string> DuplicatePaths,
    IReadOnlyList<string> DuplicateSymbols,
    string? TargetedGuidance)
{
    /// <summary>Whether this failure warrants routing to the knowledge node for research.</summary>
    public bool ShouldResearch => Class != BuildFailureClass.SelfInflictedDuplicate;
}

/// <summary>
/// Research-gating classifier (Step B, decision: reserve research for genuine knowledge gaps).
/// The dominant failure mode observed in diagnostics was the 7B model re-emitting a file it had
/// already written, producing CS0101 (duplicate type) / CS0111 (duplicate member). Routing that to
/// deep research wasted the escalation and returned generic "check your SDK" noise. This classifier
/// detects those and produces targeted guidance instead, so research is reserved for real gaps.
/// </summary>
public static class BuildFailureClassifier
{
    // e.g.  path/to/File.cs(5,23): error CS0101: The namespace 'X' already contains a definition for 'Foo'
    private static readonly Regex Cs0101 = new(
        @"^(?<path>.+?)\(\d+,\d+\):\s*error\s+CS0101:\s*The namespace '(?<ns>[^']+)' already contains a definition for '(?<sym>[^']+)'",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // e.g.  path/to/File.cs(26,17): error CS0111: Type 'Foo' already defines a member called 'Bar' with ...
    private static readonly Regex Cs0111 = new(
        @"^(?<path>.+?)\(\d+,\d+\):\s*error\s+CS0111:\s*Type '(?<type>[^']+)' already defines a member called '(?<sym>[^']+)'",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static BuildFailureClassification Classify(string? buildOutput)
    {
        if (string.IsNullOrWhiteSpace(buildOutput))
            return new BuildFailureClassification(BuildFailureClass.KnowledgeGap, Array.Empty<string>(), Array.Empty<string>(), null);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Cs0101.Matches(buildOutput))
        {
            paths.Add(NormalizePath(m.Groups["path"].Value));
            symbols.Add(m.Groups["sym"].Value);
        }
        foreach (Match m in Cs0111.Matches(buildOutput))
        {
            paths.Add(NormalizePath(m.Groups["path"].Value));
            symbols.Add(m.Groups["sym"].Value);
        }

        if (symbols.Count == 0)
            return new BuildFailureClassification(BuildFailureClass.KnowledgeGap, Array.Empty<string>(), Array.Empty<string>(), null);

        return new BuildFailureClassification(
            BuildFailureClass.SelfInflictedDuplicate,
            paths.ToList(),
            symbols.ToList(),
            BuildGuidance(paths, symbols));
    }

    private static string NormalizePath(string raw) => raw.Trim().Trim('"');

    /// <summary>
    /// The targeted message injected back to the agent instead of research — naming the offending
    /// paths and the types/members already defined, telling it not to re-emit them.
    /// </summary>
    private static string BuildGuidance(IEnumerable<string> paths, IEnumerable<string> symbols)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DUPLICATE DEFINITION — this is a self-inflicted error, not a knowledge gap.");
        sb.Append("You previously created file(s) ");
        sb.Append(string.Join(", ", paths.Select(p => $"'{p}'")));
        sb.Append(" which already define ");
        sb.Append(string.Join(", ", symbols.Select(s => $"'{s}'")));
        sb.AppendLine(".");
        sb.AppendLine("Do NOT emit a FILE block for those paths again, and do NOT redefine those types/members. " +
                      "There must be exactly one definition. If you need a change, edit the single existing file in place; " +
                      "otherwise leave it untouched and continue with the remaining work.");
        return sb.ToString();
    }
}
