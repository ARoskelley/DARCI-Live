using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Darci.Tools.Engineering;

namespace Darci.Api;

public sealed class EngineeringCollectionPartArtifact
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? PartType { get; init; }
    public Dictionary<string, double>? Parameters { get; init; }
    public bool Success { get; init; }
    public string PartDir { get; init; } = "";
    public List<string> Files { get; init; } = new();
    public Dictionary<string, float>? BoundingBoxMm { get; init; }
    public int? TriangleCount { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? RxDeg { get; init; }
    public double? RyDeg { get; init; }
    public double? RzDeg { get; init; }
    public string? Error { get; init; }
    public string? GenerationSource { get; init; }
    public List<EngineeringProviderAttempt> ProviderAttempts { get; init; } = new();
}

public sealed class EngineeringCollectionBundle
{
    public string OutputDir { get; init; } = "";
    public string? ZipPath { get; init; }
    public List<string> FilesWritten { get; init; } = new();
    public List<EngineeringCollectionPartArtifact> Parts { get; init; } = new();
}

public sealed class EngineeringAssemblyConnection
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Relation { get; init; } = "";
}

public static class EngineeringCollectionBundler
{
    public static EngineeringCollectionBundle Create(
        string contentRootPath,
        string collectionName,
        IReadOnlyList<EngineeringCollectionPartArtifact> parts,
        IReadOnlyList<EngineeringAssemblyConnection> connections,
        EngineeringValidationReport validation,
        bool createZip)
    {
        var repoRoot = EngineeringOutputBundler.ResolveRepoRoot(contentRootPath);
        var baseDir = Path.Combine(repoRoot, "tmp", "engineering");
        Directory.CreateDirectory(baseDir);

        var slug = EngineeringOutputBundler.Slugify(collectionName);
        var dirName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{slug}_{Guid.NewGuid().ToString("N")[..8]}";
        var outputDir = Path.Combine(baseDir, dirName);
        Directory.CreateDirectory(outputDir);

        var files = new List<string>();

        var partsRoot = Path.Combine(outputDir, "parts");
        Directory.CreateDirectory(partsRoot);
        foreach (var part in parts)
        {
            var partFolderName = EngineeringOutputBundler.Slugify(part.Name);
            var partTarget = Path.Combine(partsRoot, partFolderName);
            CopyDirectory(part.PartDir, partTarget);
        }

        var bomPath = Path.Combine(outputDir, "bom.csv");
        WriteBomCsv(bomPath, parts);
        files.Add(bomPath);

        var diagramPath = Path.Combine(outputDir, "assembly-diagram.mmd");
        File.WriteAllText(diagramPath, BuildMermaid(collectionName, parts, connections));
        files.Add(diagramPath);

        var assemblyManifest = BuildAssemblyManifest(partsRoot, parts);
        var assemblyJsonPath = Path.Combine(outputDir, "assembly.json");
        File.WriteAllText(assemblyJsonPath, JsonSerializer.Serialize(assemblyManifest, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        files.Add(assemblyJsonPath);

        var scadPath = Path.Combine(outputDir, "assembly-preview.scad");
        File.WriteAllText(scadPath, BuildScadPreview(assemblyManifest));
        files.Add(scadPath);

        var validationPath = Path.Combine(outputDir, "validation-report.json");
        File.WriteAllText(validationPath, JsonSerializer.Serialize(validation, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        files.Add(validationPath);

        var summaryPath = Path.Combine(outputDir, "collection-summary.json");
        var summary = new
        {
            createdAtUtc = DateTime.UtcNow,
            collectionName,
            partCount = parts.Count,
            successCount = parts.Count(p => p.Success),
            failureCount = parts.Count(p => !p.Success),
            connections,
            validation = new
            {
                validation.Passed,
                issueCount = validation.Issues.Count,
                errorCount = validation.Issues.Count(i => i.Severity == "error"),
                warningCount = validation.Issues.Count(i => i.Severity == "warning")
            },
            parts = parts.Select(p => new
            {
                p.Name,
                p.Description,
                p.PartType,
                p.Parameters,
                p.Success,
                p.GenerationSource,
                providerAttemptCount = p.ProviderAttempts.Count,
                providerFailures = p.ProviderAttempts.Where(a => !a.Success),
                p.PartDir,
                p.Files,
                p.BoundingBoxMm,
                p.TriangleCount,
                p.X,
                p.Y,
                p.Z,
                p.RxDeg,
                p.RyDeg,
                p.RzDeg,
                p.Error
            })
        };
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        files.Add(summaryPath);

        string? zipPath = null;
        if (createZip)
        {
            zipPath = Path.Combine(baseDir, $"{dirName}.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(outputDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            files.Add(zipPath);
        }

        return new EngineeringCollectionBundle
        {
            OutputDir = outputDir,
            ZipPath = zipPath,
            FilesWritten = files,
            Parts = parts.ToList()
        };
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var child = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectory(dir, child);
        }
    }

    private static void WriteBomCsv(string path, IReadOnlyList<EngineeringCollectionPartArtifact> parts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("part_name,part_type,description,success,generation_source,bbox_x,bbox_y,bbox_z,triangles,part_dir");
        foreach (var part in parts)
        {
            sb.Append('"').Append(part.Name.Replace("\"", "\"\"")).Append("\",");
            sb.Append('"').Append((part.PartType ?? "").Replace("\"", "\"\"")).Append("\",");
            sb.Append('"').Append(part.Description.Replace("\"", "\"\"")).Append("\",");
            sb.Append(part.Success ? "true" : "false").Append(',');
            sb.Append('"').Append((part.GenerationSource ?? "").Replace("\"", "\"\"")).Append("\",");
            sb.Append(part.BoundingBoxMm?.GetValueOrDefault("x", 0).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append(part.BoundingBoxMm?.GetValueOrDefault("y", 0).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append(part.BoundingBoxMm?.GetValueOrDefault("z", 0).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append(part.TriangleCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "").Append(',');
            sb.Append('"').Append(part.PartDir.Replace("\"", "\"\"")).Append('"');
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string BuildMermaid(
        string collectionName,
        IReadOnlyList<EngineeringCollectionPartArtifact> parts,
        IReadOnlyList<EngineeringAssemblyConnection> connections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        sb.AppendLine($"  subgraph {SanitizeNode(collectionName)}[\"{collectionName}\"]");
        foreach (var part in parts)
        {
            var node = SanitizeNode(part.Name);
            var label = part.Success ? part.Name : $"{part.Name} (failed)";
            sb.AppendLine($"    {node}[\"{label}\"]");
        }
        sb.AppendLine("  end");

        foreach (var c in connections)
        {
            var from = SanitizeNode(c.From);
            var to = SanitizeNode(c.To);
            var rel = string.IsNullOrWhiteSpace(c.Relation) ? "connects" : c.Relation.Replace("\"", "'");
            sb.AppendLine($"  {from} -->|\"{rel}\"| {to}");
        }

        return sb.ToString();
    }

    private static string SanitizeNode(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "node" : value;
        var chars = v.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var normalized = new string(chars);
        if (char.IsDigit(normalized[0]))
        {
            normalized = "n_" + normalized;
        }

        return normalized;
    }

    private static AssemblyManifest BuildAssemblyManifest(
        string partsRoot,
        IReadOnlyList<EngineeringCollectionPartArtifact> parts)
    {
        var placed = new List<AssemblyPart>();
        var cursorX = 0.0;
        const double gap = 20.0;

        foreach (var part in parts)
        {
            var partFolderName = EngineeringOutputBundler.Slugify(part.Name);
            var localDir = Path.Combine(partsRoot, partFolderName);
            var stl = Directory.Exists(localDir)
                ? Directory.GetFiles(localDir, "*.stl", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;

            var dx = part.BoundingBoxMm?.GetValueOrDefault("x", 40f) ?? 40f;
            var px = part.X ?? cursorX;
            var py = part.Y ?? 0.0;
            var pz = part.Z ?? 0.0;
            var rx = part.RxDeg ?? 0.0;
            var ry = part.RyDeg ?? 0.0;
            var rz = part.RzDeg ?? 0.0;

            cursorX = px + dx + gap;

            var rel = stl != null ? Path.GetRelativePath(partsRoot, stl).Replace('\\', '/') : null;
            placed.Add(new AssemblyPart
            {
                Name = part.Name,
                PartType = part.PartType,
                StlRelativePath = rel,
                X = px,
                Y = py,
                Z = pz,
                RxDeg = rx,
                RyDeg = ry,
                RzDeg = rz
            });
        }

        return new AssemblyManifest { Parts = placed };
    }

    private static string BuildScadPreview(AssemblyManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated assembly preview");
        sb.AppendLine("// Open in OpenSCAD, then Render/Export STL if needed.");
        sb.AppendLine("union() {");
        foreach (var p in manifest.Parts.Where(p => !string.IsNullOrWhiteSpace(p.StlRelativePath)))
        {
            sb.AppendLine($"  translate([{Fmt(p.X)}, {Fmt(p.Y)}, {Fmt(p.Z)}]) rotate([{Fmt(p.RxDeg)}, {Fmt(p.RyDeg)}, {Fmt(p.RzDeg)}]) import(\"parts/{p.StlRelativePath}\");");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Fmt(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class AssemblyManifest
    {
        public List<AssemblyPart> Parts { get; init; } = new();
    }

    private sealed class AssemblyPart
    {
        public string Name { get; init; } = "";
        public string? PartType { get; init; }
        public string? StlRelativePath { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
        public double RxDeg { get; init; }
        public double RyDeg { get; init; }
        public double RzDeg { get; init; }
    }
}
