using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Engineering;

/// <summary>
/// HTTP client for the Geometry Workbench Python service.
///
/// Expects the service running at http://localhost:8001
/// (started via: cd Darci.Engineering.Workbench && uvicorn main:app --port 8001)
///
/// All endpoints match the FastAPI routes defined in main.py.
/// If the service is unreachable, methods return safe defaults and log warnings —
/// DARCI degrades gracefully rather than crashing.
/// </summary>
public class GeometryWorkbenchClient : IEngineeringTool
{
    private readonly HttpClient _http;
    private readonly ILogger<GeometryWorkbenchClient> _logger;
    private readonly JsonSerializerOptions _json;

    public string ToolId       => "geometry_workbench";
    public string DisplayName  => "Geometry Workbench";
    public int StateDimensions => 64;
    public int ActionCount     => 20;

    public GeometryWorkbenchClient(HttpClient http, ILogger<GeometryWorkbenchClient> logger)
    {
        _http   = http;
        _logger = logger;
        _http.BaseAddress = new Uri("http://localhost:8001");
        _http.Timeout     = TimeSpan.FromSeconds(30);
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    // ─── Health ──────────────────────────────────────────────────────────────

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/workbench/health", ct);
            if (!response.IsSuccessStatusCode) return false;
            var health = await response.Content.ReadFromJsonAsync<WorkbenchHealthResponse>(_json, ct);
            return health?.Status == "alive";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geometry workbench health check failed");
            return false;
        }
    }

    // ─── Reset ───────────────────────────────────────────────────────────────

    public async Task<WorkbenchResetResponse> ResetAsync(
        WorkbenchResetRequest? request = null, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(
                "/workbench/reset", request ?? new WorkbenchResetRequest(), _json, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Workbench reset failed: {Status}", response.StatusCode);
                return new WorkbenchResetResponse
                {
                    State      = new float[StateDimensions],
                    ActionMask = Enumerable.Repeat(true, ActionCount).ToArray()
                };
            }

            return await response.Content.ReadFromJsonAsync<WorkbenchResetResponse>(_json, ct)
                ?? new WorkbenchResetResponse
                {
                    State      = new float[StateDimensions],
                    ActionMask = Enumerable.Repeat(true, ActionCount).ToArray()
                };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workbench reset failed");
            return new WorkbenchResetResponse
            {
                State      = new float[StateDimensions],
                ActionMask = Enumerable.Repeat(true, ActionCount).ToArray()
            };
        }
    }

    // ─── State ───────────────────────────────────────────────────────────────

    public async Task<float[]> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/workbench/state", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Workbench get-state failed: {Status}", response.StatusCode);
                return new float[StateDimensions];
            }

            var stateResp = await response.Content.ReadFromJsonAsync<WorkbenchStateResponse>(_json, ct);
            return stateResp?.State ?? new float[StateDimensions];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workbench get-state failed");
            return new float[StateDimensions];
        }
    }

    // ─── Action mask ─────────────────────────────────────────────────────────

    public async Task<bool[]> GetActionMaskAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/workbench/action-mask", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Workbench get-action-mask failed: {Status}", response.StatusCode);
                return Enumerable.Repeat(true, ActionCount).ToArray();
            }

            var maskResp = await response.Content.ReadFromJsonAsync<WorkbenchActionMaskResponse>(_json, ct);
            return maskResp?.Mask ?? Enumerable.Repeat(true, ActionCount).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workbench get-action-mask failed");
            return Enumerable.Repeat(true, ActionCount).ToArray();
        }
    }

    // ─── Execute ─────────────────────────────────────────────────────────────

    public async Task<ToolStepResult> ExecuteAsync(
        int actionId, float[] parameters, CancellationToken ct = default)
    {
        try
        {
            var request = new WorkbenchExecuteRequest
            {
                ActionId   = actionId,
                Parameters = parameters,
            };
            var response = await _http.PostAsJsonAsync("/workbench/execute", request, _json, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Workbench execute failed: {Status} {Body}",
                    response.StatusCode, body);
                return new ToolStepResult
                {
                    Success      = false,
                    ErrorMessage = $"HTTP {response.StatusCode}: {body}",
                    State        = new float[StateDimensions],
                };
            }

            return await response.Content.ReadFromJsonAsync<ToolStepResult>(_json, ct)
                ?? new ToolStepResult { Success = false, ErrorMessage = "Null response" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workbench execute failed for action {ActionId}", actionId);
            return new ToolStepResult
            {
                Success      = false,
                ErrorMessage = ex.Message,
                State        = new float[StateDimensions],
            };
        }
    }

    // ─── Validate ────────────────────────────────────────────────────────────

    public async Task<ToolValidationResult> ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/workbench/validate", null, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Workbench validate failed: {Status}", response.StatusCode);
                return new ToolValidationResult { Passed = false, OverallScore = 0f };
            }

            return await response.Content.ReadFromJsonAsync<ToolValidationResult>(_json, ct)
                ?? new ToolValidationResult { Passed = false, OverallScore = 0f };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workbench validate failed");
            return new ToolValidationResult { Passed = false, OverallScore = 0f };
        }
    }

    // ─── Undo ────────────────────────────────────────────────────────────────

    public async Task<bool> UndoAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/workbench/undo", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workbench undo failed");
            return false;
        }
    }
}
