using Darci.Shared;

namespace Darci.Brain;

/// <summary>
/// A ring buffer that stores DQN training tuples (state, action, reward, next_state).
///
/// Experiences are accumulated during live operation and sampled randomly
/// during training to break temporal correlations (experience replay).
/// When the buffer reaches capacity, the oldest entries are evicted.
/// </summary>
public interface IExperienceBuffer
{
    /// <summary>Create the backing store if it does not already exist.</summary>
    Task InitializeAsync();

    /// <summary>
    /// Persist one experience tuple.
    /// If the buffer is at capacity, the oldest entry is deleted first.
    /// </summary>
    Task StoreAsync(Experience experience);

    /// <summary>
    /// Return a random sample of up to <paramref name="batchSize"/> experiences.
    /// Returns fewer if the buffer holds fewer entries than requested.
    /// </summary>
    Task<IReadOnlyList<Experience>> SampleAsync(int batchSize);

    /// <summary>Total number of experiences currently stored.</summary>
    Task<int> CountAsync();

    /// <summary>Delete all stored experiences (used for testing / fresh starts).</summary>
    Task ClearAsync();
}
