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

    public string ToolId            => "geometry_workbench";
    public int    StateDimensions   => 64;
    public int    ActionCount       => 20;
    public int    ParameterDimensions => 6;
    public bool   IsAvailable       => _isAvailable;

    private const string InputName      = "state_vector";
    private const string LogitsOutput   = "action_logits";
    private const string ParamsOutput   = "action_params";

    public OnnxGeometryNetwork(ILogger<OnnxGeometryNetwork> logger, string modelPath)
    {
        _logger = logger;

        if (!File.Exists(modelPath))
        {
            _logger.LogWarning(
                "Geometry ONNX model not found at {Path}. " +
                "Engineering network unavailable — using exploration fallback.",
                modelPath);
            return;
        }

        try
        {
            _session     = new InferenceSession(modelPath);
            _isAvailable = true;
            _logger.LogInformation("Geometry network loaded from {Path}", modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load geometry ONNX model from {Path}", modelPath);
        }
    }

    // ─── Action selection ────────────────────────────────────────────────────

    public (int actionId, float[] parameters) SelectAction(float[] state, bool[] actionMask)
    {
        if (!_isAvailable || _session == null)
        {
            // Exploration fallback: random valid action, zero parameters
            var validActions = Enumerable.Range(0, actionMask.Length)
                .Where(i => i < actionMask.Length && actionMask[i])
                .ToList();
            var action = validActions.Count > 0
                ? validActions[Random.Shared.Next(validActions.Count)]
                : 0;
            return (action, new float[ParameterDimensions]);
        }

        var logits    = PredictLogits(state);
        var allParams = PredictAllParams(state);

        // Mask invalid actions
        for (int i = 0; i < actionMask.Length && i < logits.Length; i++)
            if (!actionMask[i]) logits[i] = float.NegativeInfinity;

        // Argmax over masked logits
        int bestAction = 0;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] > bestScore)
            {
                bestScore  = logits[i];
                bestAction = i;
            }
        }

        // Extract the 6 params for the chosen action
        var parameters = new float[ParameterDimensions];
        int offset = bestAction * ParameterDimensions;
        if (offset + ParameterDimensions <= allParams.Length)
            Array.Copy(allParams, offset, parameters, 0, ParameterDimensions);

        return (bestAction, parameters);
    }

    public float[] PredictLogits(float[] state)
    {
        if (_session == null) return new float[ActionCount];

        var tensor  = new DenseTensor<float>(state, new[] { 1, StateDimensions });
        var inputs  = new[] { NamedOnnxValue.CreateFromTensor(InputName, tensor) };
        using var outputs = _session.Run(inputs);
        return outputs.First(o => o.Name == LogitsOutput).AsTensor<float>().ToArray();
    }

    public float[] PredictParameters(float[] state, int actionId)
    {
        var allParams  = PredictAllParams(state);
        var parameters = new float[ParameterDimensions];
        int offset     = actionId * ParameterDimensions;
        if (offset + ParameterDimensions <= allParams.Length)
            Array.Copy(allParams, offset, parameters, 0, ParameterDimensions);
        return parameters;
    }

    private float[] PredictAllParams(float[] state)
    {
        if (_session == null) return new float[ActionCount * ParameterDimensions];

        var tensor  = new DenseTensor<float>(state, new[] { 1, StateDimensions });
        var inputs  = new[] { NamedOnnxValue.CreateFromTensor(InputName, tensor) };
        using var outputs = _session.Run(inputs);
        return outputs.First(o => o.Name == ParamsOutput).AsTensor<float>().ToArray();
    }

    // ─── Hot-swap ────────────────────────────────────────────────────────────

    public async Task LoadModelAsync(string path)
    {
        await Task.Yield(); // keep async signature consistent with interface

        if (!File.Exists(path))
        {
            _logger.LogWarning("Cannot hot-load geometry model — file not found: {Path}", path);
            return;
        }

        try
        {
            var newSession = new InferenceSession(path);
            var old        = _session;
            _session       = newSession;
            _isAvailable   = true;
            old?.Dispose();
            _logger.LogInformation("Geometry network hot-loaded from {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hot-load geometry model from {Path}", path);
        }
    }

    public void Dispose() => _session?.Dispose();
}
