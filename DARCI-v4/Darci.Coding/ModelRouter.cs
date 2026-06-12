#nullable enable

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Routes text generation and embedding requests to the appropriate Ollama model.
/// Reads base URL and model names from environment variables.
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly HttpClient _http;
    private readonly ILogger<ModelRouter> _logger;
    private readonly string _generalModel;
    private readonly string _codingModel;
    private readonly string _fastCodingModel;
    private readonly string _embedModel;

    public ModelRouter(HttpClient http, ILogger<ModelRouter> logger)
    {
        _http = http;
        _logger = logger;

        _generalModel = FirstNonEmpty(
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_MODEL"),
            "gemma4:e4b");

        _codingModel = FirstNonEmpty(
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_CODING_MODEL"),
            "qwen2.5-coder:7b");

        _fastCodingModel = FirstNonEmpty(
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_FAST_CODING_MODEL"),
            _codingModel);

        _embedModel = FirstNonEmpty(
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_EMBED_MODEL"),
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_EMBEDDING_MODEL"),
            "nomic-embed-text");

        var baseUrl = NormalizeBaseUrl(
            Environment.GetEnvironmentVariable("DARCI_OLLAMA_BASE_URL"),
            Environment.GetEnvironmentVariable("OLLAMA_HOST"),
            "http://localhost:11434");

        _http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        // Generous timeout: on modest local hardware with a contended Ollama, a full-file coding
        // prompt can take many minutes. A timeout here is handled gracefully (returns "") so the
        // agent loop retries rather than dying.
        _http.Timeout = TimeSpan.FromMinutes(12);

        _logger.LogInformation(
            "ModelRouter using Ollama at {BaseUrl} — general={General}, coding={Coding}, fast={Fast}, embed={Embed}",
            baseUrl, _generalModel, _codingModel, _fastCodingModel, _embedModel);
    }

    public async Task<string> GenerateAsync(string prompt, ModelTaskType taskType = ModelTaskType.General, CancellationToken ct = default)
    {
        var model = taskType switch
        {
            ModelTaskType.Coding => _codingModel,
            ModelTaskType.FastCoding => _fastCodingModel,
            _ => _generalModel
        };

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model,
                prompt,
                stream = false,
                options = new { temperature = 0.4, num_predict = 4096 }
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            _logger.LogDebug("ModelRouter.Generate model={Model} promptLen={Len}", model, prompt.Length);

            var response = await _http.PostAsync("/api/generate", content, ct);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            return doc.RootElement.TryGetProperty("response", out var r)
                ? r.GetString()?.Trim() ?? ""
                : "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation — propagate so the loop can stop cleanly.
            throw;
        }
        catch (Exception ex)
        {
            // Includes HttpClient.Timeout, which surfaces as TaskCanceledException even though the
            // caller's token was NOT cancelled. Treat it (and all transport errors) as a soft failure
            // so the agent loop can retry the step instead of dying with an unhandled exception.
            _logger.LogWarning(ex, "ModelRouter.Generate failed/timed out for model {Model}", model);
            return "";
        }
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { model = _embedModel, input = text });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/embed", content, ct);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("embeddings", out var embs)
                && embs.GetArrayLength() > 0)
            {
                var first = embs[0];
                var result = new float[first.GetArrayLength()];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = first[i].GetSingle();
                }

                return result;
            }

            return Array.Empty<float>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // HttpClient.Timeout surfaces as TaskCanceledException with the caller token un-cancelled.
            _logger.LogDebug(ex, "ModelRouter.GetEmbedding failed/timed out (non-fatal)");
            return Array.Empty<float>();
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }

        return "";
    }

    private static string NormalizeBaseUrl(params string?[] values)
    {
        var url = FirstNonEmpty(values);
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = $"http://{url}";
        }

        return url.TrimEnd('/');
    }
}
