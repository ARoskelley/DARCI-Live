#nullable enable

using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Computes and caches embeddings for each workspace file's path + first-500-char preview.
/// Called in the background after workspace import. Falls back gracefully if Ollama is unavailable.
/// </summary>
public sealed class WorkspaceEmbeddingService : IWorkspaceEmbeddingService
{
    private const int PreviewChars = 500;

    private readonly ICodingWorkspaceStore _store;
    private readonly IModelRouter _router;
    private readonly ILogger<WorkspaceEmbeddingService> _logger;

    public WorkspaceEmbeddingService(
        ICodingWorkspaceStore store,
        IModelRouter router,
        ILogger<WorkspaceEmbeddingService> logger)
    {
        _store = store;
        _router = router;
        _logger = logger;
    }

    public async Task EnrichAsync(string workspaceId, CancellationToken ct = default)
    {
        var workspace = await _store.GetWorkspaceAsync(workspaceId, ct);
        if (workspace is null)
        {
            _logger.LogWarning("WorkspaceEmbeddingService: workspace {Id} not found.", workspaceId);
            return;
        }

        var files = await _store.GetFilesAsync(workspaceId, 10_000, ct);
        var textFiles = files.Where(f => f.IsText).ToList();

        _logger.LogInformation("Starting embedding pass for workspace {Id}: {Count} text files.",
            workspaceId, textFiles.Count);

        var succeeded = 0;
        var failed = 0;

        foreach (var file in textFiles)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var input = BuildEmbeddingInput(workspace.RootPath, file);
                if (string.IsNullOrWhiteSpace(input)) continue;

                var embedding = await _router.GetEmbeddingAsync(input, ct);
                if (embedding.Length == 0)
                {
                    failed++;
                    continue;
                }

                await _store.UpsertFileEmbeddingAsync(file.Id, workspaceId, embedding, ct);
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogDebug(ex, "Embedding failed for file {Path} (non-fatal).", file.RelativePath);
            }
        }

        _logger.LogInformation(
            "Embedding pass complete for workspace {Id}: {Succeeded} succeeded, {Failed} failed.",
            workspaceId, succeeded, failed);
    }

    private static string BuildEmbeddingInput(string rootPath, CodingFileEntry file)
    {
        var preview = ReadPreview(rootPath, file);
        return string.IsNullOrWhiteSpace(preview)
            ? file.RelativePath
            : $"{file.RelativePath}\n{preview}";
    }

    private static string ReadPreview(string rootPath, CodingFileEntry file)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, file.RelativePath));
            var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            if (!File.Exists(fullPath)) return "";

            using var reader = new StreamReader(fullPath);
            var buffer = new char[PreviewChars];
            var read = reader.Read(buffer, 0, PreviewChars);
            return new string(buffer, 0, read);
        }
        catch
        {
            return "";
        }
    }
}
