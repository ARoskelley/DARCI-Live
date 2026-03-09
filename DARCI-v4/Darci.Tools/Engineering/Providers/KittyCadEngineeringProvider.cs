using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Tools.Engineering.Providers;

public class KittyCadEngineeringProvider : IEngineeringCadProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<KittyCadEngineeringProvider> _logger;
    private readonly string? _baseUrl;
    private readonly string? _apiKey;
    private readonly string _path;

    public KittyCadEngineeringProvider(HttpClient http, ILogger<KittyCadEngineeringProvider> logger)
    {
        _http = http;
        _logger = logger;
        _baseUrl = Environment.GetEnvironmentVariable("DARCI_KITTYCAD_BASE_URL");
        _apiKey = Environment.GetEnvironmentVariable("DARCI_KITTYCAD_API_KEY");
        _path = Environment.GetEnvironmentVariable("DARCI_KITTYCAD_PATH") ?? "/v1/text-to-cad";

        _http.Timeout = TimeSpan.FromSeconds(90);
        if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri))
        {
            _http.BaseAddress = uri;
        }
    }

    public string Name => "kittycad";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_apiKey);

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
            using var msg = new HttpRequestMessage(HttpMethod.Post, _path);
            msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var dims = new Dictionary<string, float?>();
            if (request.Dimensions != null)
            {
                dims["length_mm"] = request.Dimensions.LengthMm;
                dims["width_mm"] = request.Dimensions.WidthMm;
                dims["height_mm"] = request.Dimensions.HeightMm;
            }

            var payload = new
            {
                prompt = request.Description,
                description = request.Description,
                part_type = request.PartType,
                parameters = request.Parameters,
                dimensions = dims,
                output_format = "cadquery",
                format = "cadquery"
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
                    "KittyCAD provider returned status {Status}: {Body}",
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
            _logger.LogWarning(ex, "KittyCAD provider call failed");
            return new EngineeringProviderScriptResult
            {
                ProviderName = Name,
                Success = false,
                Error = ex.Message
            };
        }
    }
}
