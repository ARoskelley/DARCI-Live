namespace Darci.Brain;

/// <summary>
/// Converts DARCI's current world-state into a fixed-size numerical vector
/// that the Decision Network can process.
///
/// The output is always float[28] with values in [-1, 1] or [0, 1].
/// No English passes through this interface — only numbers.
/// </summary>
public interface IStateEncoder
{
    /// <summary>
    /// The number of dimensions in the state vector. Always 28.
    /// Matches the Decision Network's input layer size.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Encode a perception snapshot into a 28-dimensional float array.
    /// </summary>
    /// <param name="input">Flat numeric summary of State + Perception from Core.</param>
    /// <returns>A new float[28] with all values clamped to their defined ranges.</returns>
    float[] Encode(EncoderInput input);
}
