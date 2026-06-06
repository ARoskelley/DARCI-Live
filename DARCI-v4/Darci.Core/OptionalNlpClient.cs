using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Core;

public interface INlpClient
{
    bool IsReachable { get; }
    Task<bool> PingAsync(CancellationToken ct = default);
    Task<NlpComprehensionResult?> ComprehendAsync(string text, CancellationToken ct = default);
    Task<NlpExtractionResult?> ExtractAsync(string text, CancellationToken ct = default);
}

public enum NlpIntentType
{
    Unknown,
    Conversation,
    Question,
    Task,
    GoalUpdate,
    Research,
    CAD,
    EngineeringCollection,
    StatusCheck,
    Feedback,
    DecisionReference
}

public sealed record NlpComprehensionResult
{
    public NlpIntentType PrimaryIntent { get; init; } = NlpIntentType.Unknown;
    public string? ExtractedTopic { get; init; }
    public Dictionary<string, string> Entities { get; init; } = new();
    public float[] IntentDistribution { get; init; } = Array.Empty<float>();
    public float LinguisticUrgency { get; init; }
}

public sealed record NlpExtractionResult
{
    public Dictionary<string, string> Fields { get; init; } = new();
}

public sealed class NoopNlpClient : INlpClient
{
    public static NoopNlpClient Instance { get; } = new();

    private NoopNlpClient()
    {
    }

    public bool IsReachable => false;
    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task<NlpComprehensionResult?> ComprehendAsync(string text, CancellationToken ct = default) => Task.FromResult<NlpComprehensionResult?>(null);
    public Task<NlpExtractionResult?> ExtractAsync(string text, CancellationToken ct = default) => Task.FromResult<NlpExtractionResult?>(null);
}

public sealed class OptionalHttpNlpClient : INlpClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OptionalHttpNlpClient> _logger;
    private readonly bool _enabled;
    private bool _isReachable;
    private bool _hasLoggedUnavailable;

    public OptionalHttpNlpClient(HttpClient http, ILogger<OptionalHttpNlpClient> logger, string baseUrl, bool enabled)
    {
        _http = http;
        _logger = logger;
        _enabled = enabled;
        _isReachable = enabled;

        if (_enabled)
        {
            _http.BaseAddress = new Uri(NormalizeBaseUrl(baseUrl), UriKind.Absolute);
            _http.Timeout = TimeSpan.FromSeconds(5);
        }
    }

    public bool IsReachable => _enabled && _isReachable;

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (!_enabled)
        {
            return false;
        }

        foreach (var path in new[] { "/health", "/ping", "/" })
        {
            try
            {
                using var response = await _http.GetAsync(path, ct);
                if ((int)response.StatusCode < 500)
                {
                    _isReachable = true;
                    return true;
                }
            }
            catch
            {
                // Try the next common health route before logging.
            }
        }

        MarkUnavailable("Optional NLP service is not reachable.");
        return false;
    }

    public async Task<NlpComprehensionResult?> ComprehendAsync(string text, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var path in new[] { "/comprehend", "/api/comprehend" })
        {
            try
            {
                using var response = await _http.PostAsJsonAsync(path, new { text }, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    MarkUnavailable($"Optional NLP comprehend returned HTTP {(int)response.StatusCode}.");
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                _isReachable = true;
                return ParseComprehension(doc.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkUnavailable($"Optional NLP comprehend failed: {ex.Message}");
                return null;
            }
        }

        MarkUnavailable("Optional NLP comprehend endpoint was not found.");
        return null;
    }

    public async Task<NlpExtractionResult?> ExtractAsync(string text, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var response = await _http.PostAsJsonAsync("/extract", new { text }, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.ToString();
            }

            _isReachable = true;
            return new NlpExtractionResult { Fields = fields };
        }
        catch
        {
            return null;
        }
    }

    private void MarkUnavailable(string message)
    {
        _isReachable = false;
        if (_hasLoggedUnavailable)
        {
            return;
        }

        _hasLoggedUnavailable = true;
        _logger.LogWarning("{Message} Falling back to DARCI's local intent classifier.", message);
    }

    private static NlpComprehensionResult ParseComprehension(JsonElement root)
    {
        var intentRaw = TryGetString(root, "primaryIntent", "primary_intent", "intent", "type");

        return new NlpComprehensionResult
        {
            PrimaryIntent = ParseIntent(intentRaw),
            ExtractedTopic = TryGetString(root, "extractedTopic", "extracted_topic", "topic"),
            Entities = TryGetStringDictionary(root, "entities", "parameters"),
            IntentDistribution = TryGetFloatArray(root, "intentDistribution", "intent_distribution"),
            LinguisticUrgency = TryGetFloat(root, "linguisticUrgency", "linguistic_urgency", "urgency") ?? 0f
        };
    }

    private static NlpIntentType ParseIntent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NlpIntentType.Unknown;
        }

        var normalized = value.Trim()
            .Replace("_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-", "", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

        return normalized switch
        {
            "conversation" => NlpIntentType.Conversation,
            "question" => NlpIntentType.Question,
            "task" => NlpIntentType.Task,
            "goalupdate" => NlpIntentType.GoalUpdate,
            "research" => NlpIntentType.Research,
            "cad" => NlpIntentType.CAD,
            "engineeringcollection" => NlpIntentType.EngineeringCollection,
            "statuscheck" => NlpIntentType.StatusCheck,
            "feedback" => NlpIntentType.Feedback,
            "decisionreference" => NlpIntentType.DecisionReference,
            _ => NlpIntentType.Unknown
        };
    }

    private static string? TryGetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                var index = value.GetInt32();
                return Enum.IsDefined(typeof(NlpIntentType), index)
                    ? ((NlpIntentType)index).ToString()
                    : null;
            }
        }

        return null;
    }

    private static float? TryGetFloat(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetSingle(out var result))
            {
                return result;
            }
        }

        return null;
    }

    private static float[] TryGetFloatArray(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return value.EnumerateArray()
                .Select(item => item.TryGetSingle(out var result) ? result : 0f)
                .ToArray();
        }

        return Array.Empty<float>();
    }

    private static Dictionary<string, string> TryGetStringDictionary(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in value.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.ToString();
            }

            return dict;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeBaseUrl(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value)
            ? "http://localhost:5200"
            : value.Trim();

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"http://{trimmed}";
        }

        return trimmed.TrimEnd('/');
    }
}
