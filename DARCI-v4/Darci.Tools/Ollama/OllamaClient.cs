using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Darci.Tools.Ollama;

/// <summary>
/// Interface for LLM text generation
/// </summary>
public interface IOllamaClient
{
    Task<string> Generate(string prompt);
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

    public OllamaClient(
        HttpClient http,
        ILogger<OllamaClient> logger,
        IConfiguration configuration)
    {
        _http = http;
        _logger = logger;
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

    public async Task<string> Generate(string prompt)
    {
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
    }

    public async Task<List<float>> GetEmbedding(string text)
    {
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
