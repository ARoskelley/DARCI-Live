using System.Text.Json;
using Darci.Core;
using Darci.Engineering;
using Microsoft.Extensions.Logging;

namespace Darci.Api;

/// <summary>
/// IAutonomousBundler implementation for the autonomous living-loop path.
/// Creates a timestamped folder, copies the exported STL (if present),
/// and writes a manifest.json. Returns the folder path.
/// </summary>
public sealed class AutonomousBundler : IAutonomousBundler
{
    private readonly string _contentRootPath;
    private readonly ILogger<AutonomousBundler> _logger;

    public AutonomousBundler(string contentRootPath, ILogger<AutonomousBundler> logger)
    {
        _contentRootPath = contentRootPath;
        _logger = logger;
    }

    public async Task<string?> CreateAsync(
        string description,
        EngineeringResult result,
        CancellationToken ct)
    {
        try
        {
            var repoRoot = EngineeringOutputBundler.ResolveRepoRoot(_contentRootPath);
            var baseDir = Path.Combine(repoRoot, "tmp", "engineering", "autonomous");
            Directory.CreateDirectory(baseDir);

            var slug = EngineeringOutputBundler.Slugify(description);
            var dirName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{slug}_{Guid.NewGuid().ToString("N")[..8]}";
            var outputDir = Path.Combine(baseDir, dirName);
            Directory.CreateDirectory(outputDir);

            var filesWritten = new List<string>();

            // Copy STL if available
            if (!string.IsNullOrWhiteSpace(result.ExportedStlPath) && File.Exists(result.ExportedStlPath))
            {
                var stlDest = Path.Combine(outputDir, Path.GetFileName(result.ExportedStlPath));
                File.Copy(result.ExportedStlPath, stlDest, overwrite: true);
                filesWritten.Add(stlDest);
            }

            // Write manifest
            var manifest = new
            {
                description,
                generatedAt = DateTime.UtcNow,
                success = result.Success,
                finalScore = result.FinalScore,
                validationPassed = result.ValidationPassed,
                stepsTaken = result.StepsTaken,
                exportedStlPath = result.ExportedStlPath,
                errorMessage = result.ErrorMessage,
            };
            var manifestPath = Path.Combine(outputDir, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                ct);
            filesWritten.Add(manifestPath);

            _logger.LogInformation(
                "AutonomousBundler: created bundle at {Dir} ({Count} files)",
                outputDir, filesWritten.Count);

            return outputDir;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutonomousBundler: failed to create bundle for '{Desc}'", description);
            return null;
        }
    }
}
