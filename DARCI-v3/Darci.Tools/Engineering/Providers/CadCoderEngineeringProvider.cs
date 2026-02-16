using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Tools.Engineering.Providers;

public class CadCoderEngineeringProvider : IEngineeringCadProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<CadCoderEngineeringProvider> _logger;
    private readonly string? _endpoint;
    private readonly string? _apiKey;

    public CadCoderEngineeringProvider(HttpClient http, ILogger<CadCoderEngineeringProvider> logger)
    {
        _http = http;
        _logger = logger;
        _endpoint = Environment.GetEnvironmentVariable("DARCI_CADCODER_URL");
        _apiKey = Environment.GetEnvironmentVariable("DARCI_CADCODER_API_KEY");
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    public string Name => "cadcoder";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_endpoint);

    public async Task<EngineeringProviderScriptResult?> TryGenerateScript(
        EngineeringProviderRequest request,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var payload = new
            {
                prompt = request.Description,
                part_type = request.PartType,
                parameters = request.Parameters,
                dimensions = request.Dimensions == null
                    ? null
                    : new
                    {
                        length_mm = request.Dimensions.LengthMm,
                        width_mm = request.Dimensions.WidthMm,
                        height_mm = request.Dimensions.HeightMm
                    }
            };

            msg.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await _http.SendAsync(msg, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "CadCoder provider returned status {Status}: {Body}",
                    (int)response.StatusCode,
                    body.Length > 300 ? body[..300] : body);

                return new EngineeringProviderScriptResult
                {
                    ProviderName = Name,
                    Success = false,
                    Error = $"HTTP {(int)response.StatusCode}"
                };
            }

            var script = ProviderResponseParser.ExtractScript(body);
            if (string.IsNullOrWhiteSpace(script))
            {
                return new EngineeringProviderScriptResult
                {
                    ProviderName = Name,
                    Success = false,
                    Error = "No CAD script found in provider response."
                };
            }

            return new EngineeringProviderScriptResult
            {
                ProviderName = Name,
                Success = true,
                Script = script
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CadCoder provider call failed");
            return new EngineeringProviderScriptResult
            {
                ProviderName = Name,
                Success = false,
                Error = ex.Message
            };
        }
    }
}
