#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

public sealed class WorkspaceScanner : IWorkspaceScanner
{
    private const int MaxFiles = 10_000;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode",
        "bin", "obj", "node_modules", ".venv", "venv", "__pycache__",
        ".pytest_cache", "dist", "build", "coverage", "Data", "tmp"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".cache", ".onnx", ".data", ".pt",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".pdf", ".zip",
        ".7z", ".tar", ".gz", ".stl", ".step", ".obj"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".json", ".md", ".txt", ".xml", ".yml", ".yaml",
        ".props", ".targets", ".config", ".ps1", ".sh", ".py", ".ipynb", ".js",
        ".jsx", ".ts", ".tsx", ".css", ".scss", ".html", ".razor", ".sql", ".toml",
        ".ini", ".env", ".gitignore", ".dockerfile", ".java", ".kt", ".swift", ".go",
        ".rs", ".cpp", ".c", ".h", ".hpp"
    };

    private readonly ICodingWorkspaceStore _store;
    private readonly IWorkspaceEmbeddingService _embeddingService;
    private readonly IKgEnrichmentService _kgEnrichment;
    private readonly ILogger<WorkspaceScanner> _logger;

    public WorkspaceScanner(
        ICodingWorkspaceStore store,
        IWorkspaceEmbeddingService embeddingService,
        IKgEnrichmentService kgEnrichment,
        ILogger<WorkspaceScanner> logger)
    {
        _store = store;
        _embeddingService = embeddingService;
        _kgEnrichment = kgEnrichment;
        _logger = logger;
    }

    public async Task<CodingWorkspaceImportResult> ImportAsync(CodingWorkspaceImportRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            throw new ArgumentException("RootPath is required.", nameof(request));
        }

        var rootPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.RootPath));
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Workspace root was not found: {rootPath}");
        }

        var root = new DirectoryInfo(rootPath);
        if (root.Parent is null)
        {
            throw new InvalidOperationException("Refusing to import a filesystem root as a coding workspace.");
        }

        var workspaceId = Guid.NewGuid().ToString("N");
        var warnings = new List<string>();
        var files = ScanFiles(root, workspaceId, warnings, ct);
        var languages = DetectLanguages(files);
        var commands = DetectCommands(root.FullName, files);
        var summary = BuildSummary(root, files, languages, commands, warnings);

        var workspace = new CodingWorkspace
        {
            Id = workspaceId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? root.Name : request.Name.Trim(),
            RootPath = root.FullName,
            CreatedBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? "DARCI" : request.CreatedBy.Trim(),
            Tags = request.Tags ?? Array.Empty<string>(),
            Status = "active",
            Summary = summary,
            DetectedLanguages = languages,
            DetectedCommands = commands,
            CreatedAt = DateTime.UtcNow,
            LastIndexedAt = DateTime.UtcNow
        };

        await _store.UpsertWorkspaceAsync(workspace, files, ct);
        _logger.LogInformation("Imported coding workspace {WorkspaceId} at {RootPath} with {FileCount} files.",
            workspace.Id, workspace.RootPath, files.Count);

        // Fire background enrichment passes — do not await, do not block the import response.
        var enrichId = workspace.Id;
        _ = Task.Run(async () =>
        {
            try { await _embeddingService.EnrichAsync(enrichId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Background embedding pass failed for workspace {Id}.", enrichId); }
        });
        _ = Task.Run(async () =>
        {
            try { await _kgEnrichment.EnrichAsync(enrichId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Background KG enrichment pass failed for workspace {Id}.", enrichId); }
        });

        var skipped = warnings.Count(w => w.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase));
        return new CodingWorkspaceImportResult(workspace, files, skipped, warnings.ToArray());
    }

    private static List<CodingFileEntry> ScanFiles(DirectoryInfo root, string workspaceId, List<string> warnings, CancellationToken ct)
    {
        var files = new List<CodingFileEntry>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        while (stack.Count > 0 && files.Count < MaxFiles)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<DirectoryInfo> childDirs;
            try
            {
                childDirs = dir.EnumerateDirectories("*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                warnings.Add($"Skipped directory '{SafeRelative(root.FullName, dir.FullName)}': {ex.Message}");
                continue;
            }

            foreach (var child in childDirs)
            {
                if (IgnoredDirectories.Contains(child.Name) || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                stack.Push(child);
            }

            IEnumerable<FileInfo> childFiles;
            try
            {
                childFiles = dir.EnumerateFiles("*", options);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                warnings.Add($"Skipped files in '{SafeRelative(root.FullName, dir.FullName)}': {ex.Message}");
                continue;
            }

            foreach (var file in childFiles)
            {
                if (files.Count >= MaxFiles)
                {
                    warnings.Add($"Skipped remaining files after reaching scan cap of {MaxFiles}.");
                    break;
                }

                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var extension = GetExtension(file.Name);
                if (IgnoredExtensions.Contains(extension) || file.Length > MaxFileBytes)
                {
                    continue;
                }

                files.Add(new CodingFileEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    WorkspaceId = workspaceId,
                    RelativePath = SafeRelative(root.FullName, file.FullName),
                    Extension = extension,
                    Kind = ClassifyKind(file.Name, extension),
                    SizeBytes = file.Length,
                    IsText = IsTextFile(file.Name, extension),
                    LastModifiedUtc = file.LastWriteTimeUtc
                });
            }
        }

        return files
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string[] DetectLanguages(IReadOnlyList<CodingFileEntry> files)
    {
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in files.Select(f => f.Extension))
        {
            switch (ext.ToLowerInvariant())
            {
                case ".cs": languages.Add("C#"); break;
                case ".py": languages.Add("Python"); break;
                case ".js":
                case ".jsx": languages.Add("JavaScript"); break;
                case ".ts":
                case ".tsx": languages.Add("TypeScript"); break;
                case ".kt": languages.Add("Kotlin"); break;
                case ".java": languages.Add("Java"); break;
                case ".go": languages.Add("Go"); break;
                case ".rs": languages.Add("Rust"); break;
                case ".cpp":
                case ".c":
                case ".h":
                case ".hpp": languages.Add("C/C++"); break;
            }
        }

        return languages.OrderBy(x => x).ToArray();
    }

    private static string[] DetectCommands(string rootPath, IReadOnlyList<CodingFileEntry> files)
    {
        var commands = new List<string>();
        var paths = files.Select(f => f.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var solution = files.FirstOrDefault(f => f.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase));
        if (solution is not null)
        {
            commands.Add($"dotnet build \"{solution.RelativePath}\"");
            commands.Add($"dotnet test \"{solution.RelativePath}\"");
        }
        else
        {
            var csproj = files.FirstOrDefault(f => f.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase));
            if (csproj is not null)
            {
                commands.Add($"dotnet build \"{csproj.RelativePath}\"");
            }
        }

        if (paths.Contains("package.json"))
        {
            commands.AddRange(DetectNpmCommands(Path.Combine(rootPath, "package.json")));
        }

        if (paths.Contains("pyproject.toml") || paths.Contains("pytest.ini") || files.Any(f => f.RelativePath.StartsWith("tests", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add("python -m pytest");
        }

        if (paths.Contains("Cargo.toml"))
        {
            commands.Add("cargo test");
            commands.Add("cargo build");
        }

        if (paths.Contains("go.mod"))
        {
            commands.Add("go test ./...");
        }

        return commands.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> DetectNpmCommands(string packageJsonPath)
    {
        var commands = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (doc.RootElement.TryGetProperty("scripts", out var scripts))
            {
                if (scripts.TryGetProperty("build", out _))
                {
                    commands.Add("npm run build");
                }

                if (scripts.TryGetProperty("test", out _))
                {
                    commands.Add("npm test");
                }

                if (scripts.TryGetProperty("lint", out _))
                {
                    commands.Add("npm run lint");
                }
            }
        }
        catch
        {
            commands.Add("npm test");
        }

        return commands.Count > 0 ? commands : new[] { "npm test" };
    }

    private static string BuildSummary(
        DirectoryInfo root,
        IReadOnlyList<CodingFileEntry> files,
        string[] languages,
        string[] commands,
        IReadOnlyList<string> warnings)
    {
        var languageText = languages.Length == 0 ? "unknown languages" : string.Join(", ", languages);
        var commandText = commands.Length == 0 ? "no build/test commands detected" : $"{commands.Length} candidate command(s)";
        return $"{root.Name}: {files.Count} indexed file(s), {languageText}, {commandText}, {warnings.Count} warning(s).";
    }

    private static string ClassifyKind(string fileName, string extension)
    {
        var lowerName = fileName.ToLowerInvariant();
        if (lowerName is "readme.md" or "license" or "changelog.md") return "documentation";
        if (lowerName is "package.json" or "pyproject.toml" or "cargo.toml" or "go.mod") return "project-config";
        if (extension is ".sln" or ".csproj" or ".props" or ".targets") return "build-config";
        if (lowerName.Contains("test") || lowerName.Contains("spec")) return "test";
        if (extension is ".md" or ".txt") return "documentation";
        return extension is ".json" or ".yml" or ".yaml" or ".xml" or ".toml" or ".ini" ? "config" : "source";
    }

    private static bool IsTextFile(string fileName, string extension)
    {
        if (TextExtensions.Contains(extension)) return true;
        return fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExtension(string fileName)
    {
        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return ".dockerfile";
        if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase)) return ".makefile";
        if (fileName.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)) return ".gitignore";
        return Path.GetExtension(fileName);
    }

    private static string SafeRelative(string rootPath, string path)
    {
        var relative = Path.GetRelativePath(rootPath, path);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
