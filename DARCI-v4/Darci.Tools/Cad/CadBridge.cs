using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Darci.Tools.Cad;

/// <summary>
/// HTTP client for the Python CAD engine service.
/// Follows the same pattern as OllamaClient — thin HTTP wrapper,
/// no business logic. Orchestration happens in Toolkit.
/// </summary>
public interface ICadBridge
{
    Task<CadGenerateResponse?> Generate(CadGenerateRequest request);
    Task<string?> GetFeedbackPrompt(string originalRequest, CadGenerateResponse cadResult);
    Task<bool> IsHealthy();
}

public class CadBridge : ICadBridge
{
    private readonly HttpClient _http;
    private readonly ILogger<CadBridge> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public CadBridge(HttpClient http, ILogger<CadBridge> logger)
    {
        _http = http;
        _logger = logger;

        // Python CAD service runs on port 8000
        _http.BaseAddress = new Uri("http://localhost:8000");
        _http.Timeout = TimeSpan.FromSeconds(60);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<CadGenerateResponse?> Generate(CadGenerateRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogDebug("Sending CadQuery script to CAD engine ({Length} chars)",
                request.Script.Length);

            var response = await _http.PostAsync("/cad/generate", content);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<CadGenerateResponse>(_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CAD engine generate call failed");
            return null;
        }
    }

    public async Task<string?> GetFeedbackPrompt(
        string originalRequest, CadGenerateResponse cadResult)
    {
        try
        {
            var payload = new CadFeedbackRequest
            {
                OriginalRequest = originalRequest,
                CadResult = cadResult
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/cad/feedback-prompt", content);
            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<CadFeedbackResponse>(_jsonOptions);
            return result?.FeedbackPrompt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CAD engine feedback-prompt call failed");
            return null;
        }
    }

    public async Task<bool> IsHealthy()
    {
        try
        {
            var result = await Generate(new CadGenerateRequest
            {
                Script = "import cadquery as cq\nresult = cq.Workplane('XY').box(10, 10, 10)",
                Filename = "health_check.stl"
            });
            return result?.Success == true;
        }
        catch
        {
            return false;
        }
    }
}
