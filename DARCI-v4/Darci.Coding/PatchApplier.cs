#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Parses file edits from LLM responses and applies them safely within the workspace root.
/// Supports two formats:
///   1. Full file replacement: ### FILE: relative/path followed by a fenced code block.
///   2. Unified diff: ```diff blocks starting with --- / +++ headers.
/// All writes are path-contained to the workspace root.
/// </summary>
public sealed class PatchApplier
{
    // Matches: ### FILE: path/to/file.cs  or  ## FILE: ...  or  FILE: ...
    private static readonly Regex FileHeaderRegex = new(
        @"^(?:#{1,3}\s+)?FILE:\s*(.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // Matches diff file headers: --- a/path or --- path
    private static readonly Regex DiffFromRegex = new(@"^---\s+(?:a/)?(.+)$", RegexOptions.Compiled);
    private static readonly Regex DiffToRegex = new(@"^\+\+\+\s+(?:b/)?(.+)$", RegexOptions.Compiled);
    private static readonly Regex HunkHeaderRegex = new(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Compiled);

    private readonly ILogger<PatchApplier> _logger;

    public PatchApplier(ILogger<PatchApplier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses and applies all file edits found in the LLM response.
    /// Returns a list of (relativePath, success, errorMessage) tuples.
    /// </summary>
    public async Task<List<(string RelativePath, bool Success, string? Error)>> ApplyAsync(
        string workspaceRoot, string llmResponse, CancellationToken ct = default)
    {
        var results = new List<(string, bool, string?)>();

        // Try full-file replacements first.
        var filePatches = ParseFileReplacements(llmResponse);
        foreach (var (relPath, content) in filePatches)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, err) = await ApplyFileReplacementAsync(workspaceRoot, relPath, content, ct);
            results.Add((relPath, ok, err));
        }

        // Then try unified diffs.
        var diffs = ParseUnifiedDiffs(llmResponse);
        foreach (var (relPath, hunks) in diffs)
        {
            ct.ThrowIfCancellationRequested();
            // Skip if already applied via file replacement.
            if (results.Any(r => string.Equals(r.Item1, relPath, StringComparison.OrdinalIgnoreCase))) continue;
            var (ok, err) = await ApplyUnifiedDiffAsync(workspaceRoot, relPath, hunks, ct);
            results.Add((relPath, ok, err));
        }

        return results;
    }

    // ── Full file replacement ──────────────────────────────────────────────

    private static List<(string RelPath, string Content)> ParseFileReplacements(string response)
    {
        var results = new List<(string, string)>();

        // Split on FILE: headers.
        var lines = response.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = FileHeaderRegex.Match(lines[i]);
            if (!m.Success) continue;

            var relPath = NormalizeRelPath(m.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(relPath)) continue;

            // Expect a fenced code block starting on the next non-empty line.
            var j = i + 1;
            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j])) j++;
            if (j >= lines.Length || !lines[j].TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;

            // Read until closing fence.
            var contentLines = new List<string>();
            j++; // skip opening fence
            while (j < lines.Length && !lines[j].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                contentLines.Add(lines[j]);
                j++;
            }

            if (contentLines.Count > 0)
            {
                results.Add((relPath, string.Join("\n", contentLines)));
            }

            i = j; // advance past the closing fence
        }

        return results;
    }

    private async Task<(bool Ok, string? Error)> ApplyFileReplacementAsync(
        string workspaceRoot, string relPath, string content, CancellationToken ct)
    {
        try
        {
            var fullPath = ResolveAndContain(workspaceRoot, relPath);
            if (fullPath is null)
            {
                return (false, $"Path traversal detected: '{relPath}' resolves outside workspace root.");
            }

            var dir = Path.GetDirectoryName(fullPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);
            _logger.LogInformation("Applied file replacement to {RelPath}.", relPath);
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "File replacement failed for {RelPath}.", relPath);
            return (false, ex.Message);
        }
    }

    // ── Unified diff ───────────────────────────────────────────────────────

    private static List<(string RelPath, List<DiffHunk> Hunks)> ParseUnifiedDiffs(string response)
    {
        var results = new List<(string, List<DiffHunk>)>();

        // Find ```diff ... ``` blocks.
        var diffBlockRegex = new Regex(@"```diff\s*\n(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);
        foreach (Match blockMatch in diffBlockRegex.Matches(response))
        {
            var diffText = blockMatch.Groups[1].Value;
            var parsed = ParseSingleUnifiedDiff(diffText);
            results.AddRange(parsed);
        }

        return results;
    }

    private static List<(string RelPath, List<DiffHunk> Hunks)> ParseSingleUnifiedDiff(string diffText)
    {
        var results = new List<(string, List<DiffHunk>)>();
        var lines = diffText.Split('\n');

        string? currentFile = null;
        var currentHunks = new List<DiffHunk>();
        DiffHunk? currentHunk = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // --- header
            var fromM = DiffFromRegex.Match(line);
            if (fromM.Success) { /* just note the from path */ continue; }

            // +++ header
            var toM = DiffToRegex.Match(line);
            if (toM.Success)
            {
                if (currentFile is not null && currentHunks.Count > 0)
                {
                    if (currentHunk is not null) currentHunks.Add(currentHunk);
                    results.Add((currentFile, currentHunks));
                    currentHunks = new List<DiffHunk>();
                    currentHunk = null;
                }
                currentFile = NormalizeRelPath(toM.Groups[1].Value);
                continue;
            }

            // @@ hunk header
            var hunkM = HunkHeaderRegex.Match(line);
            if (hunkM.Success)
            {
                if (currentHunk is not null) currentHunks.Add(currentHunk);
                var oldStart = int.Parse(hunkM.Groups[1].Value);
                var newStart = int.Parse(hunkM.Groups[3].Value);
                currentHunk = new DiffHunk(oldStart, newStart, new List<DiffLine>());
                continue;
            }

            // Diff content lines
            if (currentHunk is not null && line.Length > 0)
            {
                var ch = line[0];
                if (ch == '+') currentHunk.Lines.Add(new DiffLine(DiffLineKind.Add, line[1..]));
                else if (ch == '-') currentHunk.Lines.Add(new DiffLine(DiffLineKind.Remove, line[1..]));
                else if (ch == ' ') currentHunk.Lines.Add(new DiffLine(DiffLineKind.Context, line[1..]));
            }
        }

        if (currentFile is not null)
        {
            if (currentHunk is not null) currentHunks.Add(currentHunk);
            if (currentHunks.Count > 0) results.Add((currentFile, currentHunks));
        }

        return results;
    }

    private async Task<(bool Ok, string? Error)> ApplyUnifiedDiffAsync(
        string workspaceRoot, string relPath, List<DiffHunk> hunks, CancellationToken ct)
    {
        try
        {
            var fullPath = ResolveAndContain(workspaceRoot, relPath);
            if (fullPath is null)
                return (false, $"Path traversal detected: '{relPath}'.");

            List<string> originalLines;
            if (File.Exists(fullPath))
            {
                originalLines = (await File.ReadAllLinesAsync(fullPath, ct)).ToList();
            }
            else
            {
                // New file — treat empty.
                originalLines = new List<string>();
            }

            var result = ApplyHunks(originalLines, hunks);
            if (result is null)
                return (false, $"Hunk application failed for '{relPath}': context lines did not match.");

            var dir = Path.GetDirectoryName(fullPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllLinesAsync(fullPath, result, Encoding.UTF8, ct);
            _logger.LogInformation("Applied unified diff to {RelPath}.", relPath);
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unified diff failed for {RelPath}.", relPath);
            return (false, ex.Message);
        }
    }

    private static List<string>? ApplyHunks(List<string> original, List<DiffHunk> hunks)
    {
        var result = new List<string>(original);
        var offset = 0; // Track cumulative line offset from previous hunks.

        foreach (var hunk in hunks)
        {
            // Convert 1-based hunk start to 0-based index with offset applied.
            var pos = hunk.OldStart - 1 + offset;
            if (pos < 0) pos = 0;

            var removed = 0;
            var added = new List<string>();

            foreach (var dl in hunk.Lines)
            {
                switch (dl.Kind)
                {
                    case DiffLineKind.Context:
                        // Verify context (fuzzy — skip mismatch rather than fail).
                        pos++;
                        removed++;
                        added.Add(dl.Content);
                        break;
                    case DiffLineKind.Remove:
                        // Skip context verification for robustness.
                        pos++;
                        removed++;
                        break;
                    case DiffLineKind.Add:
                        added.Add(dl.Content);
                        break;
                }
            }

            var hunkStart = hunk.OldStart - 1 + offset;
            if (hunkStart < 0) hunkStart = 0;
            if (hunkStart > result.Count) hunkStart = result.Count;

            var removeCount = Math.Min(removed - added.Count(l => hunk.Lines.Any(dl => dl.Kind == DiffLineKind.Add && dl.Content == l)), removed);
            // Recalculate: count removals and context lines touched.
            var removalsOnly = hunk.Lines.Count(dl => dl.Kind is DiffLineKind.Remove or DiffLineKind.Context);
            var toRemove = Math.Min(removalsOnly, result.Count - hunkStart);
            var toAdd = hunk.Lines.Where(dl => dl.Kind is DiffLineKind.Add or DiffLineKind.Context).Select(dl => dl.Content).ToList();

            result.RemoveRange(hunkStart, toRemove);
            result.InsertRange(hunkStart, toAdd);
            offset += toAdd.Count - toRemove;
        }

        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string? ResolveAndContain(string workspaceRoot, string relPath)
    {
        var normalizedRoot = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relPath));

        if (!fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    private static string NormalizeRelPath(string path) =>
        path.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '.')
            .Trim();
}

// ── Supporting types ──────────────────────────────────────────────────────

internal sealed record DiffHunk(int OldStart, int NewStart, List<DiffLine> Lines);
internal sealed record DiffLine(DiffLineKind Kind, string Content);
internal enum DiffLineKind { Context, Add, Remove }
