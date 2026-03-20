using Darci.Engineering;

namespace Darci.Core;

/// <summary>
/// Creates a timestamped output folder for an autonomous engineering run.
/// Returns the folder path, or null if bundling fails.
/// Implemented in Darci.Api to avoid a circular project reference.
/// </summary>
public interface IAutonomousBundler
{
    Task<string?> CreateAsync(
        string description,
        EngineeringResult result,
        CancellationToken ct);
}
