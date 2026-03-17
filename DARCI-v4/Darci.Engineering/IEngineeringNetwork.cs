namespace Darci.Engineering;

/// <summary>
/// Neural network that drives an engineering tool.
/// Handles the hybrid discrete + continuous action space:
/// discrete action selection (which operation) + continuous parameters (dimensions, positions).
///
/// The ONNX model has two outputs:
///   "action_logits" — float[20] unnormalized action scores
///   "action_params" — float[120] (20 actions × 6 params each), tanh-bounded [-1, 1]
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
