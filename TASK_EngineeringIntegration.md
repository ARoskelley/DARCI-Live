# Claude Code Task: Build Darci.Engineering — C# Integration Layer

## Context

DARCI v4 has a working Geometry Workbench Python service (`Darci.Engineering.Workbench/`)
that exposes 3D geometry manipulation through a REST API. There is also a trained
geometry neural network (ONNX) that selects actions and parameters for that workbench.

This task builds the C# project that connects them to DARCI's living loop:
- HTTP client to call the workbench service
- ONNX inference for the geometry network
- Orchestrator that runs the geometry loop (state → network → execute → reward → repeat)
- Integration with `Decision.cs` so `WorkOnGoal` delegates engineering tasks automatically

Read `DARCI-v4/ENGINEERING_ARCHITECTURE.md` sections 1, 2, and 7 before starting.
Read `DARCI-v4/ARCHITECTURE.md` for the behavioral network patterns to follow.
Read `Darci.Brain/OnnxDecisionNetwork.cs` as the reference implementation for ONNX inference in C#.

## New Project: Darci.Engineering

Create a new C# class library project in the solution:

```
DARCI-v4/Darci.Engineering/
├── Darci.Engineering.csproj
├── IEngineeringTool.cs
├── IEngineeringNetwork.cs
├── ToolModels.cs
├── GeometryWorkbenchClient.cs
├── OnnxGeometryNetwork.cs
├── EngineeringOrchestrator.cs
└── EngineeringGoalDetector.cs
```

### Darci.Engineering.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Darci.Engineering</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.16.3" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
    <PackageReference Include="System.Text.Json" Version="8.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Darci.Shared\Darci.Shared.csproj" />
  </ItemGroup>
</Project>
```

Add a project reference from `Darci.Core` and `Darci.Api` to `Darci.Engineering`:
```xml
<ProjectReference Include="..\Darci.Engineering\Darci.Engineering.csproj" />
```

---

## File 1: ToolModels.cs — Shared Data Types

These are the C# equivalents of the Python workbench's request/response models.

```csharp
using System.Text.Json.Serialization;

namespace Darci.Engineering;

/// <summary>Result from executing one action on an engineering tool.</summary>
public record ToolStepResult
{
    [JsonPropertyName("state")]
    public float[] State { get; init; } = Array.Empty<float>();

    [JsonPropertyName("metrics")]
    public Dictionary<string, float> Metrics { get; init; } = new();

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("reward_components")]
    public Dictionary<string, float> RewardComponents { get; init; } = new();
}

/// <summary>Result from running full validation on a tool.</summary>
public record ToolValidationResult
{
    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("overall_score")]
    public float OverallScore { get; init; }

    [JsonPropertyName("category_scores")]
    public Dictionary<string, float> CategoryScores { get; init; } = new();

    [JsonPropertyName("violations")]
    public List<ToolViolation> Violations { get; init; } = new();
}

/// <summary>A specific validation failure or warning.</summary>
public record ToolViolation
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "warning";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("value")]
    public float? Value { get; init; }

    [JsonPropertyName("threshold")]
    public float? Threshold { get; init; }

    [JsonPropertyName("location")]
    public float[]? Location { get; init; }
}

/// <summary>Request to reset a workbench session.</summary>
public record WorkbenchResetRequest
{
    [JsonPropertyName("reference_path")]
    public string? ReferencePath { get; init; }

    [JsonPropertyName("constraints")]
    public Dictionary<string, object>? Constraints { get; init; }

    [JsonPropertyName("targets")]
    public Dictionary<string, float>? Targets { get; init; }
}

/// <summary>Request to execute an action.</summary>
public record WorkbenchExecuteRequest
{
    [JsonPropertyName("action_id")]
    public int ActionId { get; init; }

    [JsonPropertyName("parameters")]
    public float[] Parameters { get; init; } = new float[6];
}

/// <summary>Response from workbench reset.</summary>
public record WorkbenchResetResponse
{
    [JsonPropertyName("state")]
    public float[] State { get; init; } = Array.Empty<float>();

    [JsonPropertyName("action_mask")]
    public bool[] ActionMask { get; init; } = Array.Empty<bool>();
}

/// <summary>Response from workbench state endpoint.</summary>
public record WorkbenchStateResponse
{
    [JsonPropertyName("state")]
    public float[] State { get; init; } = Array.Empty<float>();

    [JsonPropertyName("step_count")]
    public int StepCount { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }
}

/// <summary>Response from workbench action mask endpoint.</summary>
public record WorkbenchActionMaskResponse
{
    [JsonPropertyName("mask")]
    public bool[] Mask { get; init; } = Array.Empty<bool>();

    [JsonPropertyName("valid_action_names")]
    public List<string> ValidActionNames { get; init; } = new();
}

/// <summary>Response from workbench health endpoint.</summary>
public record WorkbenchHealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("has_geometry")]
    public bool HasGeometry { get; init; }

    [JsonPropertyName("step_count")]
    public int StepCount { get; init; }
}

/// <summary>Result of one engineering orchestration loop.</summary>
public record EngineeringResult
{
    public bool Success { get; init; }
    public float FinalScore { get; init; }
    public bool ValidationPassed { get; init; }
    public int StepsTaken { get; init; }
    public float TotalReward { get; init; }
    public string? ExportedStepPath { get; init; }
    public string? ExportedStlPath { get; init; }
    public ToolValidationResult? FinalValidation { get; init; }
    public string? ErrorMessage { get; init; }
}
```

---

## File 2: IEngineeringTool.cs — Universal Tool Interface

```csharp
namespace Darci.Engineering;

/// <summary>
/// Universal interface for all engineering tools.
/// Each tool is a standalone service with a numerical interface:
/// state vector in, action + params out, quality metrics back.
///
/// Implementations:
///   - GeometryWorkbenchClient (HTTP client for Python workbench service)
///   - Future: BoardDesignerClient, SimulationBridgeClient, etc.
/// </summary>
public interface IEngineeringTool
{
    string ToolId { get; }
    string DisplayName { get; }
    int StateDimensions { get; }
    int ActionCount { get; }

    /// <summary>Check if the tool service is reachable.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);

    /// <summary>Reset the tool to a clean state. Returns initial state vector and action mask.</summary>
    Task<WorkbenchResetResponse> ResetAsync(WorkbenchResetRequest? request = null, CancellationToken ct = default);

    /// <summary>Get the current state vector.</summary>
    Task<float[]> GetStateAsync(CancellationToken ct = default);

    /// <summary>Get the current action validity mask.</summary>
    Task<bool[]> GetActionMaskAsync(CancellationToken ct = default);

    /// <summary>Execute an action with continuous parameters.</summary>
    Task<ToolStepResult> ExecuteAsync(int actionId, float[] parameters, CancellationToken ct = default);

    /// <summary>Run full validation suite.</summary>
    Task<ToolValidationResult> ValidateAsync(CancellationToken ct = default);

    /// <summary>Undo the last action.</summary>
    Task<bool> UndoAsync(CancellationToken ct = default);
}
```

---

## File 3: IEngineeringNetwork.cs — Neural Network for Engineering Tools

```csharp
namespace Darci.Engineering;

/// <summary>
/// Neural network that drives an engineering tool.
/// Handles the hybrid discrete + continuous action space:
/// discrete action selection (which operation) + continuous parameters (dimensions, positions).
///
/// The ONNX model has two outputs:
///   "action_logits" — float[20] unnormalized action scores
///   "action_params" — float[120] (20 actions × 6 params each), tanh-bounded [-1,1]
/// </summary>
public interface IEngineeringNetwork
{
    string ToolId { get; }
    int StateDimensions { get; }
    int ActionCount { get; }
    int ParameterDimensions { get; }
    bool IsAvailable { get; }

    /// <summary>
    /// Select action and parameters given current state and mask.
    /// Returns (actionId, parameters[6]).
    /// </summary>
    (int actionId, float[] parameters) SelectAction(float[] state, bool[] actionMask);

    /// <summary>Get raw action logits for confidence/logging.</summary>
    float[] PredictLogits(float[] state);

    /// <summary>Get parameters for a specific action.</summary>
    float[] PredictParameters(float[] state, int actionId);

    /// <summary>Hot-swap model without restart.</summary>
    Task LoadModelAsync(string path);
}
```

---

## File 4: GeometryWorkbenchClient.cs — HTTP Client

This calls the Python workbench service running on port 8001.
Follow the same HTTP client patterns used in `Darci.Tools/Cad/CadBridge.cs` and
`Darci.Tools/Ollama/OllamaClient.cs`.

```csharp
using System.Net.Http.Json;
using System.Text;
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
/// If the service is unreachable, methods return safe defaults and log warnings.
/// </summary>
public class GeometryWorkbenchClient : IEngineeringTool
{
    private readonly HttpClient _http;
    private readonly ILogger<GeometryWorkbenchClient> _logger;
    private readonly JsonSerializerOptions _json;

    public string ToolId => "geometry_workbench";
    public string DisplayName => "Geometry Workbench";
    public int StateDimensions => 64;
    public int ActionCount => 20;

    public GeometryWorkbenchClient(HttpClient http, ILogger<GeometryWorkbenchClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.BaseAddress = new Uri("http://localhost:8001");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    // Implement every IEngineeringTool method by calling the corresponding
    // workbench endpoint. Pattern for each:
    //
    //   1. Build request body (if POST)
    //   2. Call _http.PostAsJsonAsync or _http.GetAsync
    //   3. Check response.IsSuccessStatusCode
    //   4. Deserialize response
    //   5. Return result (or safe default on failure with warning log)
    //
    // Endpoint mapping:
    //   ResetAsync      → POST /workbench/reset
    //   GetStateAsync   → GET  /workbench/state        → return .state float[]
    //   GetActionMaskAsync → GET /workbench/action-mask → return .mask bool[]
    //   ExecuteAsync    → POST /workbench/execute       → return ToolStepResult
    //   ValidateAsync   → POST /workbench/validate      → return ToolValidationResult
    //   UndoAsync       → POST /workbench/undo          → return success bool
    //   IsHealthyAsync  → GET  /workbench/health        → return status == "alive"
    //
    // Example implementation for one method:

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

    // Implement the remaining methods following the same pattern.
    // Every method should be wrapped in try/catch and return a safe default on failure.
    // Log warnings (not errors) when the service is unreachable — DARCI should
    // degrade gracefully, not crash.

    // For ExecuteAsync specifically:
    public async Task<ToolStepResult> ExecuteAsync(int actionId, float[] parameters, CancellationToken ct = default)
    {
        try
        {
            var request = new WorkbenchExecuteRequest
            {
                ActionId = actionId,
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
                    Success = false,
                    ErrorMessage = $"HTTP {response.StatusCode}: {body}",
                    State = new float[StateDimensions],
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
                Success = false,
                ErrorMessage = ex.Message,
                State = new float[StateDimensions],
            };
        }
    }

    // ... implement ResetAsync, GetStateAsync, GetActionMaskAsync, ValidateAsync, UndoAsync
    // following the same pattern.
}
```

---

## File 5: OnnxGeometryNetwork.cs — ONNX Inference for Geometry

Follow the pattern from `Darci.Brain/OnnxDecisionNetwork.cs` but adapted for
the geometry network's dual-output format.

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Darci.Engineering;

/// <summary>
/// ONNX inference for the geometry SAC actor network.
///
/// Model contract:
///   Input:  "state_vector"   — float32[1, 64]
///   Output: "action_logits"  — float32[1, 20]
///   Output: "action_params"  — float32[1, 120]  (20 actions × 6 params, tanh-bounded)
///
/// If the model file is absent, IsAvailable = false and the orchestrator
/// falls back to random valid actions (exploration mode).
/// </summary>
public sealed class OnnxGeometryNetwork : IEngineeringNetwork, IDisposable
{
    private readonly ILogger<OnnxGeometryNetwork> _logger;
    private InferenceSession? _session;
    private bool _isAvailable;

    public string ToolId => "geometry_workbench";
    public int StateDimensions => 64;
    public int ActionCount => 20;
    public int ParameterDimensions => 6;
    public bool IsAvailable => _isAvailable;

    private const string InputName = "state_vector";
    private const string LogitsOutputName = "action_logits";
    private const string ParamsOutputName = "action_params";

    public OnnxGeometryNetwork(ILogger<OnnxGeometryNetwork> logger, string modelPath)
    {
        _logger = logger;

        if (!File.Exists(modelPath))
        {
            _logger.LogWarning(
                "Geometry ONNX model not found at {Path}. Engineering network unavailable — using exploration fallback.",
                modelPath);
            return;
        }

        try
        {
            _session = new InferenceSession(modelPath);
            _isAvailable = true;
            _logger.LogInformation("Geometry network loaded from {Path}", modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load geometry ONNX model from {Path}", modelPath);
        }
    }

    public (int actionId, float[] parameters) SelectAction(float[] state, bool[] actionMask)
    {
        if (!_isAvailable || _session == null)
        {
            // Fallback: random valid action with zero parameters
            var valid = Enumerable.Range(0, actionMask.Length).Where(i => actionMask[i]).ToList();
            var action = valid.Count > 0 ? valid[Random.Shared.Next(valid.Count)] : 0;
            return (action, new float[ParameterDimensions]);
        }

        var logits = PredictLogits(state);
        var allParams = PredictAllParams(state);

        // Apply mask
        for (int i = 0; i < actionMask.Length; i++)
            if (!actionMask[i]) logits[i] = float.NegativeInfinity;

        // Argmax (no exploration during inference — SAC entropy handles it during training)
        int bestAction = 0;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] > bestScore)
            {
                bestScore = logits[i];
                bestAction = i;
            }
        }

        // Extract params for chosen action (6 params starting at bestAction * 6)
        var parameters = new float[ParameterDimensions];
        Array.Copy(allParams, bestAction * ParameterDimensions, parameters, 0, ParameterDimensions);

        return (bestAction, parameters);
    }

    public float[] PredictLogits(float[] state)
    {
        if (_session == null) return new float[ActionCount];

        var inputTensor = new DenseTensor<float>(state, new[] { 1, StateDimensions });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(InputName, inputTensor) };

        using var outputs = _session.Run(inputs);
        // First output is action_logits
        return outputs.First(o => o.Name == LogitsOutputName).AsTensor<float>().ToArray();
    }

    public float[] PredictParameters(float[] state, int actionId)
    {
        var allParams = PredictAllParams(state);
        var parameters = new float[ParameterDimensions];
        Array.Copy(allParams, actionId * ParameterDimensions, parameters, 0, ParameterDimensions);
        return parameters;
    }

    private float[] PredictAllParams(float[] state)
    {
        if (_session == null) return new float[ActionCount * ParameterDimensions];

        var inputTensor = new DenseTensor<float>(state, new[] { 1, StateDimensions });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(InputName, inputTensor) };

        using var outputs = _session.Run(inputs);
        return outputs.First(o => o.Name == ParamsOutputName).AsTensor<float>().ToArray();
    }

    public async Task LoadModelAsync(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Cannot hot-load geometry model — file not found: {Path}", path);
            return;
        }

        try
        {
            var newSession = new InferenceSession(path);
            var old = _session;
            _session = newSession;
            _isAvailable = true;
            old?.Dispose();
            _logger.LogInformation("Geometry network hot-loaded from {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hot-load geometry model from {Path}", path);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
```

---

## File 6: EngineeringOrchestrator.cs — The Loop

This is the brain of engineering execution. When DARCI's behavioral network
selects `WorkOnGoal` and the goal is engineering-related, the orchestrator
takes over and runs the geometry loop.

```csharp
using Microsoft.Extensions.Logging;

namespace Darci.Engineering;

/// <summary>
/// Coordinates engineering tool usage in a loop:
///   1. Reset tool with goal constraints
///   2. Get state → network selects action + params → execute on tool
///   3. Check reward, repeat until validation passes or max iterations
///   4. Export result, report back to behavioral layer
///
/// This is the engineering equivalent of the behavioral living loop,
/// but runs as a sub-loop within a single WorkOnGoal action.
/// </summary>
public class EngineeringOrchestrator
{
    private readonly ILogger<EngineeringOrchestrator> _logger;
    private readonly IEngineeringTool _workbench;
    private readonly IEngineeringNetwork _network;

    // Configurable limits
    public int MaxIterations { get; set; } = 30;
    public float EarlyStopScore { get; set; } = 0.85f;

    public EngineeringOrchestrator(
        ILogger<EngineeringOrchestrator> logger,
        IEngineeringTool workbench,
        IEngineeringNetwork network)
    {
        _logger = logger;
        _workbench = workbench;
        _network = network;
    }

    /// <summary>
    /// Run a full engineering session for a goal.
    ///
    /// This method:
    ///   1. Checks if the workbench service is healthy
    ///   2. Resets the workbench with the goal's constraints
    ///   3. Runs the neural network loop (state → action → execute → repeat)
    ///   4. Validates the final result
    ///   5. Returns an EngineeringResult with score, export paths, etc.
    /// </summary>
    public async Task<EngineeringResult> RunAsync(
        EngineeringGoalSpec goal,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Engineering orchestrator starting: {Description}", goal.Description);

        // Check service health
        if (!await _workbench.IsHealthyAsync(ct))
        {
            _logger.LogWarning("Geometry workbench service is not reachable");
            return new EngineeringResult
            {
                Success = false,
                ErrorMessage = "Geometry workbench service unavailable. Start it with: uvicorn main:app --port 8001",
            };
        }

        // Reset workbench with goal constraints
        var resetResponse = await _workbench.ResetAsync(new WorkbenchResetRequest
        {
            ReferencePath = goal.ReferencePath,
            Constraints = goal.Constraints,
            Targets = goal.Targets,
        }, ct);

        var state = resetResponse.State;
        var actionMask = resetResponse.ActionMask;
        float totalReward = 0f;
        int steps = 0;

        _logger.LogDebug("Workbench reset. State dim: {Dim}, starting loop", state.Length);

        // Main engineering loop
        for (int i = 0; i < MaxIterations; i++)
        {
            if (ct.IsCancellationRequested) break;

            // Network selects action + parameters
            int actionId;
            float[] parameters;

            if (_network.IsAvailable)
            {
                (actionId, parameters) = _network.SelectAction(state, actionMask);

                // Log with confidence
                var logits = _network.PredictLogits(state);
                var confidence = SoftmaxConfidence(logits, actionMask, actionId);
                _logger.LogDebug(
                    "Engineering action: {ActionId} (confidence: {Conf:P1})",
                    actionId, confidence);
            }
            else
            {
                // Exploration fallback: random valid action
                var valid = Enumerable.Range(0, actionMask.Length).Where(j => actionMask[j]).ToList();
                actionId = valid.Count > 0 ? valid[Random.Shared.Next(valid.Count)] : 0;
                parameters = new float[6];
                _logger.LogDebug("Engineering action (random): {ActionId}", actionId);
            }

            // Execute
            var result = await _workbench.ExecuteAsync(actionId, parameters, ct);
            steps++;

            // Update state and mask
            state = result.State;
            actionMask = await _workbench.GetActionMaskAsync(ct);

            // Accumulate reward
            float stepReward = result.RewardComponents.Values.Sum();
            totalReward += stepReward;

            if (stepReward != 0)
                _logger.LogDebug("Engineering reward: {Reward:+0.00} (step {Step})", stepReward, steps);

            // Check for finalize action (19) or if network chose to stop
            if (actionId == 19) // finalize
            {
                _logger.LogInformation("Network chose to finalize at step {Step}", steps);
                break;
            }

            // Early stop if score is high enough
            if (result.Metrics.TryGetValue("printability_score", out var printScore) &&
                printScore >= EarlyStopScore)
            {
                _logger.LogInformation(
                    "Early stop: printability score {Score:F2} >= {Threshold:F2} at step {Step}",
                    printScore, EarlyStopScore, steps);
                break;
            }
        }

        // Final validation
        var validation = await _workbench.ValidateAsync(ct);

        _logger.LogInformation(
            "Engineering complete. Steps: {Steps}, Score: {Score:F2}, Passed: {Passed}, Reward: {Reward:+0.0}",
            steps, validation.OverallScore, validation.Passed, totalReward);

        return new EngineeringResult
        {
            Success = validation.Passed,
            FinalScore = validation.OverallScore,
            ValidationPassed = validation.Passed,
            StepsTaken = steps,
            TotalReward = totalReward,
            FinalValidation = validation,
        };
    }

    private static float SoftmaxConfidence(float[] logits, bool[] mask, int chosenAction)
    {
        var masked = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
            masked[i] = mask[i] ? logits[i] : float.NegativeInfinity;

        float max = masked.Max();
        var exp = masked.Select(x => x == float.NegativeInfinity ? 0f : MathF.Exp(x - max)).ToArray();
        float sum = exp.Sum();
        return sum > 0 ? exp[chosenAction] / sum : 0f;
    }
}

/// <summary>
/// Specification for an engineering goal.
/// Extracted from a DARCI goal by EngineeringGoalDetector.
/// </summary>
public record EngineeringGoalSpec
{
    public string Description { get; init; } = "";
    public string? ReferencePath { get; init; }
    public Dictionary<string, object>? Constraints { get; init; }
    public Dictionary<string, float>? Targets { get; init; }
    public string ToolId { get; init; } = "geometry_workbench";
}
```

---

## File 7: EngineeringGoalDetector.cs — Detects Engineering Goals

Determines whether a DARCI goal is an engineering task and extracts the
relevant constraints/targets.

```csharp
using Microsoft.Extensions.Logging;

namespace Darci.Engineering;

/// <summary>
/// Determines whether a DARCI goal is an engineering task that should be
/// delegated to the engineering orchestrator.
///
/// Detection is keyword-based for now. Future versions could use a small
/// classifier network, but keywords work well enough for common patterns
/// like "design a bracket", "3D print a housing", "create a part".
/// </summary>
public class EngineeringGoalDetector
{
    private readonly ILogger<EngineeringGoalDetector> _logger;

    // Keywords that indicate engineering tasks
    private static readonly string[] EngineeringKeywords = new[]
    {
        "design", "cad", "3d print", "bracket", "housing", "enclosure",
        "part", "mount", "fixture", "assembly", "mechanism", "gear",
        "shaft", "bushing", "socket", "plate", "shell", "fillet",
        "chamfer", "hole", "boss", "rib", "wall thickness", "tolerance",
        "clearance", "interference", "stl", "step file", "mesh",
        "extrude", "printable", "manufacturable", "prosthetic",
        "hydraulic", "exoskeleton", "actuator", "joint",
    };

    public EngineeringGoalDetector(ILogger<EngineeringGoalDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if a goal description indicates an engineering task.
    /// Returns null if not engineering, or an EngineeringGoalSpec if it is.
    /// </summary>
    public EngineeringGoalSpec? Detect(string goalTitle, string? goalDescription = null)
    {
        var text = $"{goalTitle} {goalDescription}".ToLowerInvariant();

        var matchCount = EngineeringKeywords.Count(kw => text.Contains(kw));

        if (matchCount == 0)
            return null;

        // Higher confidence with more keyword matches
        if (matchCount < 2)
        {
            _logger.LogDebug(
                "Goal has weak engineering signal ({Count} keyword): {Title}",
                matchCount, goalTitle);
            // Still treat it as engineering — single keyword like "bracket" is enough
        }

        _logger.LogInformation(
            "Detected engineering goal ({Count} keywords): {Title}",
            matchCount, goalTitle);

        // Extract constraints from description if present
        var constraints = ExtractConstraints(text);

        return new EngineeringGoalSpec
        {
            Description = goalTitle,
            Constraints = constraints.Count > 0 ? constraints : null,
            ToolId = "geometry_workbench",
        };
    }

    /// <summary>
    /// Simple constraint extraction from natural language.
    /// Looks for patterns like "5mm wall", "10mm hole", etc.
    /// This is a rough heuristic — the LLM can do better if needed.
    /// </summary>
    private Dictionary<string, object> ExtractConstraints(string text)
    {
        var constraints = new Dictionary<string, object>();

        // Pattern: Xmm wall → min_wall constraint
        var wallMatch = System.Text.RegularExpressions.Regex.Match(
            text, @"(\d+(?:\.\d+)?)\s*mm\s*wall");
        if (wallMatch.Success)
        {
            constraints["min_wall"] = new Dictionary<string, object>
            {
                ["type"] = "printability",
                ["min_wall"] = float.Parse(wallMatch.Groups[1].Value),
            };
        }

        // Pattern: Xmm hole → feature constraint
        var holeMatch = System.Text.RegularExpressions.Regex.Match(
            text, @"(\d+(?:\.\d+)?)\s*mm\s*hole");
        if (holeMatch.Success)
        {
            constraints["has_hole"] = new Dictionary<string, object>
            {
                ["type"] = "feature",
                ["feature_type"] = "hole",
            };
        }

        return constraints;
    }
}
```

---

## Wiring Into DARCI

### Modify Decision.cs — BrainActionToDarciAction for WorkOnGoal

In `Decision.cs`, the `BrainActionToDarciAction` method's `WorkOnGoal` case
currently picks a goal and works on it through the v3 toolkit. Add engineering
detection:

Find the `BrainAction.WorkOnGoal` case in `BrainActionToDarciAction`. Before
the existing goal-work logic, add:

```csharp
case BrainAction.WorkOnGoal:
{
    var goal = /* existing logic to pick the active goal */;
    if (goal == null)
        return DarciAction.Rest(TimeSpan.FromSeconds(1), "No active goals");

    // Check if this is an engineering goal
    var engineeringSpec = _engineeringDetector?.Detect(goal.Title, goal.Description);
    if (engineeringSpec != null)
    {
        // Delegate to engineering orchestrator
        return new DarciAction
        {
            Type = ActionType.Engineering,  // NEW action type — add to ActionType enum
            Description = $"Engineering: {goal.Title}",
            GoalId = goal.Id,
            EngineeringSpec = engineeringSpec,
        };
    }

    // ... existing non-engineering goal work logic ...
}
```

This requires adding `EngineeringGoalDetector` as a dependency to `Decision`.
Also add `ActionType.Engineering` to the `ActionType` enum in Darci.Shared.

### Modify Darci.cs — Handle Engineering Actions in Act()

In `Darci.cs`, the `Act()` method needs a case for the new `ActionType.Engineering`:

```csharp
case ActionType.Engineering:
{
    if (_orchestrator == null)
    {
        _logger.LogWarning("Engineering action requested but orchestrator not available");
        return new ActionOutcome { Success = false, Error = "Engineering not configured" };
    }

    var spec = action.EngineeringSpec;
    var result = await _orchestrator.RunAsync(spec, ct);

    return new ActionOutcome
    {
        Success = result.Success,
        Description = $"Engineering completed. Score: {result.FinalScore:F2}, " +
                      $"Steps: {result.StepsTaken}, Passed: {result.ValidationPassed}",
    };
}
```

### Modify Program.cs — Register Engineering Services

Add to the DI container in `Program.cs`:

```csharp
// === Engineering Services ===

// Geometry Workbench HTTP client (Python service on port 8001)
builder.Services.AddHttpClient<IEngineeringTool, GeometryWorkbenchClient>();

// Geometry ONNX network
var geometryModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "geometry_policy.onnx");
builder.Services.AddSingleton<IEngineeringNetwork>(sp =>
    new OnnxGeometryNetwork(
        sp.GetRequiredService<ILogger<OnnxGeometryNetwork>>(),
        geometryModelPath));

// Engineering goal detector
builder.Services.AddSingleton<EngineeringGoalDetector>();

// Engineering orchestrator
builder.Services.AddSingleton<EngineeringOrchestrator>(sp =>
    new EngineeringOrchestrator(
        sp.GetRequiredService<ILogger<EngineeringOrchestrator>>(),
        sp.GetRequiredService<IEngineeringTool>(),
        sp.GetRequiredService<IEngineeringNetwork>()));
```

Update the `Decision` and `Darci` constructor registrations to include the new dependencies.

### Add API Monitoring Endpoints

```csharp
// Engineering status
app.MapGet("/engineering/neural/status", async (
    IEngineeringTool workbench,
    IEngineeringNetwork network) =>
{
    var healthy = await workbench.IsHealthyAsync();
    return Results.Ok(new
    {
        workbenchHealthy = healthy,
        networkAvailable = network.IsAvailable,
        toolId = workbench.ToolId,
        stateDimensions = workbench.StateDimensions,
        actionCount = workbench.ActionCount,
    });
});

// Run engineering task manually (for testing)
app.MapPost("/engineering/neural/run", async (
    EngineeringGoalSpec spec,
    EngineeringOrchestrator orchestrator,
    CancellationToken ct) =>
{
    var result = await orchestrator.RunAsync(spec, ct);
    return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
});

// Hot-swap geometry model
app.MapPost("/engineering/neural/load-model", async (IEngineeringNetwork network) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Models", "geometry_policy.onnx");
    if (!File.Exists(path))
        return Results.NotFound(new { error = "geometry_policy.onnx not found" });

    await network.LoadModelAsync(path);
    return Results.Ok(new { loaded = true, available = network.IsAvailable });
});
```

---

## File Summary

| File | Action |
|------|--------|
| `Darci.Engineering/Darci.Engineering.csproj` | NEW project |
| `Darci.Engineering/ToolModels.cs` | NEW — all data types |
| `Darci.Engineering/IEngineeringTool.cs` | NEW — universal tool interface |
| `Darci.Engineering/IEngineeringNetwork.cs` | NEW — neural network interface |
| `Darci.Engineering/GeometryWorkbenchClient.cs` | NEW — HTTP client for Python service |
| `Darci.Engineering/OnnxGeometryNetwork.cs` | NEW — ONNX inference |
| `Darci.Engineering/EngineeringOrchestrator.cs` | NEW — engineering execution loop |
| `Darci.Engineering/EngineeringGoalDetector.cs` | NEW — detects engineering goals |
| `Darci.Shared/Models.cs` or equivalent | MODIFY — add `ActionType.Engineering` |
| `Darci.Core/Decision.cs` | MODIFY — add engineering detection to WorkOnGoal |
| `Darci.Core/Darci.cs` | MODIFY — handle ActionType.Engineering in Act() |
| `Darci.Api/Program.cs` | MODIFY — register engineering services, add endpoints |
| `Darci.Core/Darci.Core.csproj` | MODIFY — add reference to Darci.Engineering |
| `Darci.Api/Darci.Api.csproj` | MODIFY — add reference to Darci.Engineering |

## Critical Notes

- If the workbench service isn't running, everything degrades gracefully. DARCI just can't do engineering tasks — behavioral decisions work fine.
- If the geometry ONNX model isn't present, the orchestrator uses random exploration. This still generates useful data for future training.
- The old `Darci.Tools/Engineering` code (v3 LLM-generates-scripts approach) should NOT be removed yet. It can coexist. The new system only activates when the neural network and workbench service are both available.
- The `EngineeringGoalDetector` is deliberately simple (keyword-based). It works for obvious cases. Edge cases can be refined later with an LLM classifier or a small neural network.
- The workbench service must be started separately: `cd Darci.Engineering.Workbench && uvicorn main:app --port 8001`
