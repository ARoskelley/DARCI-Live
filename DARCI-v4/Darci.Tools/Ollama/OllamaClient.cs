using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Darci.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Darci.Tools.Ollama;

/// <summary>
/// Interface for LLM text generation
/// </summary>
public interface IOllamaClient
{
    /// <summary>
    /// Generate text. <paramref name="kind"/> defaults to Background, which yields to a running
    /// coding task. Pass <see cref="ModelCallKind.Foreground"/> for calls a person is actively
    /// waiting on — those pass through under the default narrow focus policy.
    /// </summary>
    Task<string> Generate(string prompt, ModelCallKind kind = ModelCallKind.Background);

    Task<List<float>> GetEmbedding(string text);
}

/// <summary>
/// Ollama client for local LLM inference
/// </summary>
public class OllamaClient : IOllamaClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaClient> _logger;
    private readonly string _model;
    private readonly string _embeddingModel;

    /// <summary>
    /// Shared gate that serialises local model use against the coding agent loop. Optional so
    /// that any manual construction (tests, tooling) keeps working — when null, behaviour is
    /// exactly as before this was introduced.
    /// </summary>
    private readonly IModelFocus? _focus;

    public OllamaClient(
        HttpClient http,
        ILogger<OllamaClient> logger,
        IConfiguration configuration,
        IModelFocus? focus = null)
    {
        _http = http;
        _logger = logger;
        _focus = focus;
        _model = FirstNonEmpty(
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_MODEL"),
            configuration["Darci:OllamaModel"],
            "gemma4:e4b");
        _embeddingModel = FirstNonEmpty(
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_EMBEDDING_MODEL"),
            configuration["Darci:EmbeddingModel"],
            "nomic-embed-text");

        var baseUrl = NormalizeBaseUrl(
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_BASE_URL"),
            Environment.GetEnvironmentVariable("OLLAMA_HOST"),
            configuration["Darci:OllamaBaseUrl"],
            "http://localhost:11434");

        // Ollama runs locally
        _http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        _http.Timeout = TimeSpan.FromMinutes(5); // LLM can be slow

        _logger.LogInformation(
            "Using Ollama at {BaseUrl} with model {Model} and embedding model {EmbeddingModel}",
            _http.BaseAddress,
            _model,
            _embeddingModel);
    }

    /// <summary>
    /// Returned when the living loop yields its turn because the coding agent holds model focus.
    /// Mirrors the existing "[Error generating response]" convention so callers that already
    /// tolerate a bracketed sentinel keep working.
    /// </summary>
    public const string SkippedForFocus = "[Skipped: local model busy with a coding run]";

    public async Task<string> Generate(string prompt, ModelCallKind kind = ModelCallKind.Background)
    {
        // Background work yields to a running coding task rather than fighting it for VRAM.
        // Foreground (a person is waiting) passes through under the default narrow policy.
        // See Darci.Shared/ModelFocus.cs for why this is application-level and not a second
        // Ollama instance.
        IDisposable? lease = null;
        if (_focus is not null)
        {
            lease = await _focus.TryAcquireForAsync("core:generate", kind, ModelFocus.DefaultCoreWait);
            if (lease is null)
            {
                _logger.LogInformation(
                    "Skipping {Kind} core generation — model focus held by '{Holder}' for {Seconds:0}s.",
                    kind,
                    _focus.Holder ?? "another subsystem",
                    _focus.GetStatus().HeldForSeconds ?? 0);
                return SkippedForFocus;
            }
        }

        try
        {
            var request = new
            {
                model = _model,
                prompt = prompt,
                stream = false,
                options = new
                {
                    temperature = 0.7,
                    num_predict = 1024
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogDebug("Generating with {Model}, prompt length: {Length}", _model, prompt.Length);

            var response = await _http.PostAsync("/api/generate", content);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaResponse>();
            var text = result?.Response?.Trim() ?? "";

            _logger.LogDebug("Generated {Length} chars", text.Length);

            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama generation failed");
            return "[Error generating response]";
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public async Task<List<float>> GetEmbedding(string text)
    {
        IDisposable? lease = null;
        if (_focus is not null)
        {
            // Embeddings are always background — nothing user-facing blocks on one.
            lease = await _focus.TryAcquireForAsync(
                "core:embed", ModelCallKind.Background, ModelFocus.DefaultCoreWait);
            if (lease is null)
            {
                _logger.LogDebug(
                    "Skipping core embedding — model focus held by '{Holder}'.",
                    _focus.Holder ?? "another subsystem");
                return new List<float>();
            }
        }

        try
        {
            var request = new
            {
                model = _embeddingModel,
                input = text
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/embed", content);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>();
            return result?.Embeddings?.FirstOrDefault() ?? new List<float>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama embedding failed");
            return new List<float>();
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private class OllamaResponse
    {
        public string? Response { get; set; }
        public bool Done { get; set; }
    }

    private class OllamaEmbedResponse
    {
        public List<List<float>>? Embeddings { get; set; }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string NormalizeBaseUrl(params string?[] values)
    {
        var baseUrl = FirstNonEmpty(values);

        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = $"http://{baseUrl}";
        }

        return baseUrl.TrimEnd('/');
    }
}
