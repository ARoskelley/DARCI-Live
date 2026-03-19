using Darci.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Darci.Brain;

/// <summary>
/// Production implementation of <see cref="IDecisionNetwork"/> using ONNX Runtime.
///
/// Loads a trained PyTorch model exported to ONNX format.
/// If the model file is absent, <see cref="IsAvailable"/> is false and DARCI
/// falls back to the v3 priority ladder transparently.
///
/// ONNX model contract:
///   Input  — "state_vector" : float32[1, 29]
///   Output — "action_logits": float32[1, 10]
///
/// Thread safety: <see cref="InferenceSession.Run"/> is thread-safe.
/// <see cref="RecordExperience"/> is fire-and-forget and never blocks the loop.
/// </summary>
public sealed class OnnxDecisionNetwork : IDecisionNetwork, IDisposable
{
    private readonly ILogger<OnnxDecisionNetwork> _logger;
    private readonly ExperienceBuffer _buffer;

    private InferenceSession? _session;
    private bool _isAvailable;

    private float _epsilon;
    private long _trainingSteps;

    private const float EpsilonStart = 0.3f;
    private const float EpsilonMin   = 0.05f;
    private const float EpsilonDecaySteps = 10_000f;

    private const string InputName  = "state_vector";
    private const string OutputName = "action_logits";

    public bool  IsAvailable   => _isAvailable;
    public float Epsilon        => _epsilon;
    public long  TrainingSteps  => _trainingSteps;

    public OnnxDecisionNetwork(
        ILogger<OnnxDecisionNetwork> logger,
        ExperienceBuffer buffer,
        string modelPath)
    {
        _logger  = logger;
        _buffer  = buffer;
        _epsilon = EpsilonStart;

        if (!File.Exists(modelPath))
        {
            _logger.LogWarning(
                "ONNX model not found at {Path}. Neural decisions unavailable — falling back to priority ladder.",
                modelPath);
            return;
        }

        try
        {
            _session     = new InferenceSession(modelPath);
            _isAvailable = true;
            _logger.LogInformation("OnnxDecisionNetwork loaded from {Path}", modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load ONNX model from {Path}. Falling back to priority ladder.", modelPath);
        }
    }

    /// <summary>
    /// Feed a 29-dim state vector through the network and return 10 raw action logits.
    /// Returns all-zeros if the model is unavailable.
    /// </summary>
    public float[] Predict(float[] stateVector)
    {
        if (_session == null)
            return new float[10];

        var inputTensor = new DenseTensor<float>(stateVector, new[] { 1, stateVector.Length });
        var inputs      = new[] { NamedOnnxValue.CreateFromTensor(InputName, inputTensor) };

        using var outputs = _session.Run(inputs);
        return outputs.First().AsTensor<float>().ToArray();
    }

    /// <summary>
    /// Select an action using epsilon-greedy exploration over the masked action space.
    /// Invalid actions (mask[i] == false) are never chosen.
    /// </summary>
    public int SelectAction(float[] stateVector, bool[] actionMask)
    {
        var validActions = Enumerable.Range(0, actionMask.Length)
            .Where(i => actionMask[i])
            .ToList();

        // Always have at least Rest available
        if (validActions.Count == 0)
            return (int)BrainAction.Rest;

        // Explore: pick a random valid action
        if (Random.Shared.NextSingle() < _epsilon)
            return validActions[Random.Shared.Next(validActions.Count)];

        // Exploit: pick the valid action with the highest logit
        var logits = Predict(stateVector);
        return validActions.MaxBy(i => logits[i]);
    }

    /// <summary>
    /// Store one experience tuple in the buffer. Fire-and-forget — never blocks.
    /// </summary>
    public void RecordExperience(
        float[] state, int action, float reward, float[] nextState, bool isTerminal = false)
    {
        _ = _buffer.StoreAsync(new Experience
        {
            State      = state,
            Action     = action,
            Reward     = reward,
            NextState  = nextState,
            IsTerminal = isTerminal,
            Timestamp  = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Online training runs in Python. C# side collects experience only.
    /// Calling this increments the training step counter and decays epsilon.
    /// </summary>
    public Task TrainAsync(int batchSize = 32)
    {
        DecayEpsilon();
        _logger.LogDebug(
            "TrainAsync: epsilon={Epsilon:F3}, steps={Steps} — training handled offline by Python pipeline.",
            _epsilon, _trainingSteps);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Model persistence is handled by the Python pipeline. No-op on C# side.
    /// </summary>
    public Task SaveModelAsync(string path)
    {
        _logger.LogDebug("SaveModelAsync called — model persistence handled by Python.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hot-swap the loaded ONNX model without restarting DARCI.
    /// Safe to call at runtime — disposes the old session after the new one is ready.
    /// </summary>
    public async Task LoadModelAsync(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("LoadModelAsync: file not found at {Path}", path);
            return;
        }

        try
        {
            var newSession = new InferenceSession(path);
            var old        = _session;
            _session       = newSession;
            _isAvailable   = true;
            old?.Dispose();
            _logger.LogInformation("OnnxDecisionNetwork hot-swapped model from {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hot-swap ONNX model from {Path}", path);
        }

        await Task.CompletedTask;
    }

    private void DecayEpsilon()
    {
        _trainingSteps++;
        _epsilon = Math.Max(
            EpsilonMin,
            EpsilonStart - (EpsilonStart - EpsilonMin) * (_trainingSteps / EpsilonDecaySteps));
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
