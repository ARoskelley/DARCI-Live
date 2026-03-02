namespace Darci.Brain;

/// <summary>
/// The Decision Network: maps a 28-dimensional state vector to a discrete action.
///
/// This is DARCI's executive cortex. It replaces the hardcoded C# priority ladder
/// from v3 with a neural network that learns which actions lead to better outcomes.
///
/// Implementations:
///   - <c>OnnxDecisionNetwork</c>  — production: runs an ONNX model exported from PyTorch
///   - <c>FallbackDecisionNetwork</c> — development: replicates the v3 priority ladder
///     so Phase 1 data collection works before the real model is trained
///
/// Thread safety: implementations should be safe to call concurrently from
/// the DARCI living loop and the background training loop.
/// </summary>
public interface IDecisionNetwork
{
    /// <summary>
    /// Whether a trained model is loaded and available.
    /// When false, callers should fall back to the v3 priority ladder.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Compute raw action logits for a state vector.
    /// Returns float[10] — one value per action, unnormalised.
    /// Softmax + masking is applied by <see cref="SelectAction"/>.
    /// </summary>
    /// <param name="stateVector">float[28] from <see cref="IStateEncoder"/>.</param>
    float[] Predict(float[] stateVector);

    /// <summary>
    /// Select the best valid action for a state, applying epsilon-greedy
    /// exploration and an action validity mask.
    /// </summary>
    /// <param name="stateVector">float[28] from <see cref="IStateEncoder"/>.</param>
    /// <param name="actionMask">
    /// bool[10] where true = action is valid in this state.
    /// Invalid actions are set to -∞ before softmax so they never win.
    /// </param>
    /// <returns>Action ID 0–9 (cast to <see cref="Darci.Shared.BrainAction"/>).</returns>
    int SelectAction(float[] stateVector, bool[] actionMask);

    /// <summary>
    /// Record one (state, action, reward, next_state) experience for later training.
    /// This is a fire-and-forget call from the living loop — it should not block.
    /// </summary>
    void RecordExperience(float[] state, int action, float reward, float[] nextState, bool isTerminal = false);

    /// <summary>
    /// Run one training step over a random batch from the experience buffer.
    /// Called periodically (every N cycles) by the background training service.
    /// </summary>
    /// <param name="batchSize">Number of experiences to sample per update. Default 32.</param>
    Task TrainAsync(int batchSize = 32);

    /// <summary>Save model weights to disk.</summary>
    Task SaveModelAsync(string path);

    /// <summary>Load model weights from disk. Sets <see cref="IsAvailable"/> to true on success.</summary>
    Task LoadModelAsync(string path);

    /// <summary>Current epsilon used for exploration (0.05–0.5 range during training).</summary>
    float Epsilon { get; }

    /// <summary>Total number of training steps completed.</summary>
    long TrainingSteps { get; }
}
