using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
        string model = "gemma2:9b",
        string embeddingModel = "nomic-embed-text")
    {
        _http = http;
        _logger = logger;
        _model = model;
        _embeddingModel = embeddingModel;
        
        // Ollama runs locally
        _http.BaseAddress = new Uri("http://localhost:11434");
        _http.Timeout = TimeSpan.FromMinutes(5); // LLM can be slow
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
                prompt = text
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _http.PostAsync("/api/embeddings", content);
            response.EnsureSuccessStatusCode();
            
            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>();
            return result?.Embedding ?? new List<float>();
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
    
    private class OllamaEmbeddingResponse
    {
        public List<float>? Embedding { get; set; }
    }
}
