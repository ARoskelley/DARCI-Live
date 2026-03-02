using System.Text.Json;

namespace Darci.Tools.Engineering.Providers;

internal static class ProviderResponseParser
{
    private static readonly string[] ScriptFields =
    {
        "script",
        "cadquery_script",
        "cadquery",
        "python",
        "code"
    };

    private static readonly string[] ContainerFields =
    {
        "data",
        "result",
        "output",
        "response"
    };

    public static string? ExtractScript(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractScript(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractScript(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var text = el.GetString();
            if (!string.IsNullOrWhiteSpace(text) &&
                (text.Contains("result", StringComparison.Ordinal) || text.Contains("cq.", StringComparison.Ordinal)))
            {
                return text;
            }
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in ScriptFields)
        {
            if (el.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var text = val.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        foreach (var key in ContainerFields)
        {
            if (el.TryGetProperty(key, out var nested))
            {
                var nestedScript = ExtractScript(nested);
                if (!string.IsNullOrWhiteSpace(nestedScript))
                {
                    return nestedScript;
                }
            }
        }

        return null;
    }
}
