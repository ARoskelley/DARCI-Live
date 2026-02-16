using System.IO.Compression;
using System.Text.Json;
using Darci.Tools.Engineering;

namespace Darci.Api;

public sealed class EngineeringOutputBundle
{
    public string OutputDir { get; init; } = "";
    public string? ZipPath { get; init; }
    public List<string> FilesWritten { get; init; } = new();
}

public static class EngineeringOutputBundler
{
    public static EngineeringOutputBundle Create(
        string contentRootPath,
        string description,
        EngineeringWorkbenchResult result,
        bool createZip)
    {
        var repoRoot = ResolveRepoRoot(contentRootPath);
        var baseDir = Path.Combine(repoRoot, "tmp", "engineering");
        Directory.CreateDirectory(baseDir);

        var slug = Slugify(description);
        var dirName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{slug}_{Guid.NewGuid().ToString("N")[..8]}";
        var outputDir = Path.Combine(baseDir, dirName);
        var bundle = WriteToDirectory(outputDir, description, result);

        string? zipPath = null;
        if (createZip)
        {
            zipPath = Path.Combine(baseDir, $"{dirName}.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(outputDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            bundle.FilesWritten.Add(zipPath);
        }

        return new EngineeringOutputBundle
        {
            OutputDir = outputDir,
            ZipPath = zipPath,
            FilesWritten = bundle.FilesWritten
        };
    }

    public static EngineeringOutputBundle WriteToDirectory(
        string outputDir,
        string description,
        EngineeringWorkbenchResult result)
    {
        Directory.CreateDirectory(outputDir);
        var files = new List<string>();

        if (!string.IsNullOrWhiteSpace(result.FinalScript))
        {
            var scriptPath = Path.Combine(outputDir, "engineering_script.py");
            File.WriteAllText(scriptPath, result.FinalScript);
            files.Add(scriptPath);
        }

        var cad = result.CadResult;
        if (cad != null)
        {
            CopyStl(outputDir, cad.StlPath, files);
            WriteRenderArtifacts(outputDir, cad.RenderImages, files);
        }

        var summaryPath = Path.Combine(outputDir, "summary.json");
        var summary = new
        {
            createdAtUtc = DateTime.UtcNow,
            request = new { description },
            success = result.Success,
            error = result.Error,
            generationSource = result.GenerationSource,
            providerAttempts = result.ProviderAttempts,
            cadResult = result.CadResult
        };
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        files.Add(summaryPath);

        return new EngineeringOutputBundle
        {
            OutputDir = outputDir,
            FilesWritten = files
        };
    }

    public static string ResolveRepoRoot(string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(contentRootPath))
        {
            return Directory.GetCurrentDirectory();
        }

        var dir = new DirectoryInfo(contentRootPath);
        if (dir.Name.Equals("Darci.Api", StringComparison.OrdinalIgnoreCase) && dir.Parent != null)
        {
            return dir.Parent.FullName;
        }

        return dir.FullName;
    }

    private static void CopyStl(string outputDir, string? stlPath, List<string> files)
    {
        if (string.IsNullOrWhiteSpace(stlPath) || !File.Exists(stlPath))
        {
            return;
        }

        var target = Path.Combine(outputDir, Path.GetFileName(stlPath));
        File.Copy(stlPath, target, overwrite: true);
        files.Add(target);
    }

    private static void WriteRenderArtifacts(string outputDir, Dictionary<string, string?> images, List<string> files)
    {
        foreach (var kvp in images)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                continue;
            }

            var safeName = Slugify(kvp.Key);
            var value = kvp.Value!;

            if (File.Exists(value))
            {
                var ext = Path.GetExtension(value);
                if (string.IsNullOrWhiteSpace(ext))
                {
                    ext = ".png";
                }

                var target = Path.Combine(outputDir, $"{safeName}{ext}");
                File.Copy(value, target, overwrite: true);
                files.Add(target);
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(value);
                var target = Path.Combine(outputDir, $"{safeName}.png");
                File.WriteAllBytes(target, bytes);
                files.Add(target);
            }
            catch
            {
                var target = Path.Combine(outputDir, $"{safeName}.txt");
                File.WriteAllText(target, value);
                files.Add(target);
            }
        }
    }

    public static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "engineering";
        }

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        var parts = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var collapsed = string.Join("-", parts);

        if (string.IsNullOrWhiteSpace(collapsed))
        {
            return "engineering";
        }

        return collapsed.Length <= 32 ? collapsed : collapsed[..32];
    }
}
