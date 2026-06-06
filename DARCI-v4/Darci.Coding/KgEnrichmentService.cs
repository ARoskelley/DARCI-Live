#nullable enable

using System.Text.RegularExpressions;
using Darci.Memory.Graph;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Extracts type/class/function symbols from workspace source files via regex
/// and creates KG nodes for each symbol with edges back to their file.
/// </summary>
public sealed class KgEnrichmentService : IKgEnrichmentService
{
    private readonly ICodingWorkspaceStore _store;
    private readonly IKnowledgeGraph _kg;
    private readonly ILogger<KgEnrichmentService> _logger;

    // .cs: public/internal class, interface, record, enum, struct, delegate
    private static readonly Regex CsTypeRegex = new(
        @"(?:public|internal|protected|private|sealed|abstract|static)\s+(?:partial\s+)?(?:class|interface|record|enum|struct|delegate)\s+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // .cs methods: public/internal void/Task/string etc.
    private static readonly Regex CsMethodRegex = new(
        @"(?:public|internal|protected)\s+(?:static\s+|async\s+|override\s+|virtual\s+)*(?:\w+(?:<[^>]+>)?(?:\?)?)\s+(\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // .py: class and def at module level
    private static readonly Regex PyClassRegex = new(@"^class\s+(\w+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex PyFuncRegex = new(@"^def\s+(\w+)", RegexOptions.Compiled | RegexOptions.Multiline);

    // .ts/.js: exported class, interface, function, type, const, let
    private static readonly Regex TsExportRegex = new(
        @"(?:export\s+(?:default\s+)?)?(?:class|interface|function|type|const|let|var)\s+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly HashSet<string> SkipSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "while", "do", "switch", "case", "return", "new", "this",
        "base", "null", "true", "false", "var", "let", "const", "void", "int", "string",
        "bool", "float", "double", "long", "object", "dynamic", "async", "await"
    };

    public KgEnrichmentService(
        ICodingWorkspaceStore store,
        IKnowledgeGraph kg,
        ILogger<KgEnrichmentService> logger)
    {
        _store = store;
        _kg = kg;
        _logger = logger;
    }

    public async Task EnrichAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await _store.GetWorkspaceAsync(workspaceId, ct);
        if (workspace is null) return;

        var files = await _store.GetFilesAsync(workspaceId, 10_000, ct);
        var sourceFiles = files.Where(f => f.IsText && IsCodeFile(f.Extension)).ToList();

        _logger.LogInformation("KG enrichment pass for workspace {Id}: {Count} source files.",
            workspaceId, sourceFiles.Count);

        var fileEntityIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var symbolCount = 0;

        // Ensure a KG entity exists for each file.
        foreach (var file in sourceFiles)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var fileEntity = await _kg.UpsertEntityAsync(
                    name: file.RelativePath,
                    entityType: "code-file",
                    domain: "code",
                    description: $"{file.Kind} file in workspace {workspace.Name}",
                    ct: ct);
                fileEntityIds[file.Id] = fileEntity.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "KG file entity upsert failed for {Path}.", file.RelativePath);
            }
        }

        foreach (var file in sourceFiles)
        {
            if (ct.IsCancellationRequested) break;
            if (!fileEntityIds.TryGetValue(file.Id, out var fileEntityId)) continue;

            try
            {
                var content = ReadContent(workspace.RootPath, file);
                if (string.IsNullOrWhiteSpace(content)) continue;

                var symbols = ExtractSymbols(file.Extension, content);
                foreach (var symbol in symbols.Take(100))
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var symbolEntity = await _kg.UpsertEntityAsync(
                            name: symbol.Name,
                            entityType: symbol.Kind,
                            domain: "code",
                            description: $"{symbol.Kind} in {file.RelativePath}",
                            ct: ct);

                        await _kg.UpsertRelationAsync(
                            fromEntityId: fileEntityId,
                            toEntityId: symbolEntity.Id,
                            relationType: "defines",
                            weight: 1.0f,
                            confidence: 0.9f,
                            ct: ct);

                        symbolCount++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "KG symbol upsert failed for {Symbol}.", symbol.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "KG enrichment failed for file {Path}.", file.RelativePath);
            }
        }

        _logger.LogInformation("KG enrichment complete for workspace {Id}: {Count} symbols added.", workspaceId, symbolCount);
    }

    private static IEnumerable<(string Name, string Kind)> ExtractSymbols(string extension, string content)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<(string, string)> Yield(Regex rx, string kind)
        {
            foreach (Match m in rx.Matches(content))
            {
                var name = m.Groups[1].Value;
                if (name.Length >= 2 && !SkipSymbols.Contains(name) && seen.Add(name))
                {
                    yield return (name, kind);
                }
            }
        }

        return extension.ToLowerInvariant() switch
        {
            ".cs" => Yield(CsTypeRegex, "type").Concat(Yield(CsMethodRegex, "method")),
            ".py" => Yield(PyClassRegex, "class").Concat(Yield(PyFuncRegex, "function")),
            ".ts" or ".tsx" or ".js" or ".jsx" => Yield(TsExportRegex, "export"),
            _ => Enumerable.Empty<(string, string)>()
        };
    }

    private static bool IsCodeFile(string extension) =>
        extension.ToLowerInvariant() is ".cs" or ".py" or ".ts" or ".tsx" or ".js" or ".jsx";

    private static string ReadContent(string rootPath, CodingFileEntry file)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, file.RelativePath));
            var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return "";
            if (!File.Exists(fullPath)) return "";
            return File.ReadAllText(fullPath);
        }
        catch
        {
            return "";
        }
    }
}
