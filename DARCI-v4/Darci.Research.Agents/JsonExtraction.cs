#nullable enable

namespace Darci.Research.Agents;

/// <summary>Pulls a JSON object out of noisy LLM output (markdown fences, prose preamble, etc.).</summary>
internal static class JsonExtraction
{
    /// <summary>Returns the substring from the first '{' to its matching '}', or null if none.</summary>
    public static string? FirstObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }
}
