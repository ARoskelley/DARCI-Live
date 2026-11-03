#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Coding.Tests;

public class CodingWorkspaceResolverTests
{
    // ── Pure scoring core ────────────────────────────────────────────────────

    [Fact]
    public void Cosine_IdenticalVectors_IsOne()
    {
        var v = new[] { 1f, 2f, 3f };
        Assert.Equal(1.0, CodingWorkspaceResolver.Cosine(v, v), 5);
    }

    [Fact]
    public void Cosine_Orthogonal_IsZero()
    {
        Assert.Equal(0.0, CodingWorkspaceResolver.Cosine(new[] { 1f, 0f }, new[] { 0f, 1f }), 5);
    }

    [Fact]
    public void Cosine_Opposite_ClampsToZero()
    {
        Assert.Equal(0.0, CodingWorkspaceResolver.Cosine(new[] { 1f, 0f }, new[] { -1f, 0f }), 5);
    }

    [Fact]
    public void Cosine_MismatchedOrEmpty_IsZero()
    {
        Assert.Equal(0.0, CodingWorkspaceResolver.Cosine(new[] { 1f, 2f }, new[] { 1f }), 5);
        Assert.Equal(0.0, CodingWorkspaceResolver.Cosine(Array.Empty<float>(), new[] { 1f }), 5);
    }

    [Fact]
    public void SelectBestMatch_PicksHighestMaxCosineWorkspace()
    {
        var goal = new[] { 1f, 0f, 0f };
        var candidates = new List<(string, IReadOnlyList<float[]>)>
        {
            ("ws-far",   new List<float[]> { new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 1f } }),   // orthogonal → 0
            ("ws-near",  new List<float[]> { new[] { 0f, 1f, 0f }, new[] { 0.9f, 0.1f, 0f } }), // one close file
        };

        var (id, score) = CodingWorkspaceResolver.SelectBestMatch(goal, candidates);

        Assert.Equal("ws-near", id);
        Assert.True(score > 0.9, $"expected high similarity, got {score}");
    }

    [Fact]
    public void SelectBestMatch_NoEmbeddings_ScoresZero()
    {
        var goal = new[] { 1f, 0f };
        var candidates = new List<(string, IReadOnlyList<float[]>)>
        {
            ("ws-empty", new List<float[]>()),
        };
        var (id, score) = CodingWorkspaceResolver.SelectBestMatch(goal, candidates);
        Assert.Equal("ws-empty", id);
        Assert.Equal(0.0, score, 5);
    }

    [Fact]
    public void Slug_SanitizesAndStaysFilesystemSafe()
    {
        var slug = CodingWorkspaceResolver.Slug("Implement the Damm check-digit algorithm!!");
        Assert.Matches(@"^[a-z0-9-]+-\d{14}$", slug);
        Assert.DoesNotContain(" ", slug);
        Assert.DoesNotContain("!", slug);
    }

    // ── ResolveAsync (reuse vs create) with fakes ────────────────────────────

    private sealed class FakeRouter : IModelRouter
    {
        private readonly float[] _embedding;
        public FakeRouter(float[] embedding) => _embedding = embedding;
        public Task<string> GenerateAsync(string prompt, ModelTaskType taskType = ModelTaskType.General, CancellationToken ct = default)
            => Task.FromResult("");
        public Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
            => Task.FromResult(_embedding);
    }

    private sealed class FakeScanner : IWorkspaceScanner
    {
        public string? ImportedRoot;
        public Task<CodingWorkspaceImportResult> ImportAsync(CodingWorkspaceImportRequest request, CancellationToken ct = default)
        {
            ImportedRoot = request.RootPath;
            var ws = new CodingWorkspace { Id = "ws-created", Name = request.Name ?? "new", RootPath = request.RootPath };
            return Task.FromResult(new CodingWorkspaceImportResult(ws, Array.Empty<CodingFileEntry>(), 0, Array.Empty<string>()));
        }
    }

    // Minimal store fake: only the two methods the resolver uses are real.
    private sealed class FakeStore : ICodingWorkspaceStore
    {
        private readonly IReadOnlyList<CodingWorkspace> _workspaces;
        private readonly Dictionary<string, IReadOnlyDictionary<string, float[]>> _embeddings;
        public FakeStore(IReadOnlyList<CodingWorkspace> workspaces, Dictionary<string, IReadOnlyDictionary<string, float[]>> embeddings)
        {
            _workspaces = workspaces;
            _embeddings = embeddings;
        }
        public Task<IReadOnlyList<CodingWorkspace>> GetWorkspacesAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult(_workspaces);
        public Task<IReadOnlyDictionary<string, float[]>> GetFileEmbeddingsAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult(_embeddings.TryGetValue(workspaceId, out var e) ? e : new Dictionary<string, float[]>());

        // Unused by the resolver.
        public Task InitializeAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertWorkspaceAsync(CodingWorkspace workspace, IReadOnlyList<CodingFileEntry> files, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CodingWorkspace?> GetWorkspaceAsync(string workspaceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodingFileEntry>> GetFilesAsync(string workspaceId, int limit = 500, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddCommandRunAsync(CodingCommandRun run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodingCommandRun>> GetCommandRunsAsync(string workspaceId, int limit = 50, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodingCommandRun>> GetRecentCommandRunsForTaskAsync(string taskId, int limit = 10, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddTaskAsync(CodingTaskRecord task, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateTaskAsync(CodingTaskRecord task, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CodingTaskRecord?> GetTaskAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CodingTaskRecord>> GetTasksAsync(string? workspaceId = null, int limit = 50, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertFileEmbeddingAsync(string fileId, string workspaceId, float[] embedding, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddCheckpointAsync(CodingCheckpoint checkpoint, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CodingCheckpoint?> GetLatestCheckpointAsync(string workspaceId, string taskId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static CodingWorkspace Ws(string id) => new() { Id = id, Name = id };

    private string TempRoot() => Path.Combine(Path.GetTempPath(), $"darci-wsroot-{Guid.NewGuid():N}");

    private CodingWorkspaceResolver MakeResolver(FakeStore store, FakeRouter router, FakeScanner scanner, string root) =>
        new(store, router, scanner, root, NullLogger<CodingWorkspaceResolver>.Instance);

    [Fact]
    public async Task ResolveAsync_ReuseOnMatch()
    {
        var goal = new[] { 1f, 0f, 0f };
        var store = new FakeStore(
            new[] { Ws("ws-a"), Ws("ws-b") },
            new()
            {
                ["ws-a"] = new Dictionary<string, float[]> { ["f1"] = new[] { 0f, 1f, 0f } },          // dissimilar
                ["ws-b"] = new Dictionary<string, float[]> { ["f1"] = new[] { 0.98f, 0.02f, 0f } },    // strong match
            });
        var scanner = new FakeScanner();
        var resolver = MakeResolver(store, new FakeRouter(goal), scanner, TempRoot());

        var res = await resolver.ResolveAsync("work on ws-b's code");

        Assert.False(res.Created);
        Assert.Equal("ws-b", res.ContextId);
        Assert.True(res.Confidence.Score >= CodingWorkspaceResolver.ReuseThreshold);
        Assert.Null(scanner.ImportedRoot);   // nothing created
    }

    [Fact]
    public async Task ResolveAsync_CreateOnNoMatch()
    {
        var goal = new[] { 1f, 0f, 0f };
        var store = new FakeStore(
            new[] { Ws("ws-a") },
            new() { ["ws-a"] = new Dictionary<string, float[]> { ["f1"] = new[] { 0f, 1f, 0f } } }); // orthogonal → 0
        var scanner = new FakeScanner();
        var root = TempRoot();
        var resolver = MakeResolver(store, new FakeRouter(goal), scanner, root);

        var res = await resolver.ResolveAsync("a totally unrelated new project");

        Assert.True(res.Created);
        Assert.Equal("ws-created", res.ContextId);
        Assert.True(res.Confidence.IsAssessed);              // a score was computed
        Assert.True(res.Confidence.Score < CodingWorkspaceResolver.ReuseThreshold);
        Assert.NotNull(scanner.ImportedRoot);
        Assert.StartsWith(root, scanner.ImportedRoot!);
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ResolveAsync_NoWorkspaces_Creates_Unassessed()
    {
        var store = new FakeStore(Array.Empty<CodingWorkspace>(), new());
        var scanner = new FakeScanner();
        var root = TempRoot();
        var resolver = MakeResolver(store, new FakeRouter(new[] { 1f, 0f }), scanner, root);

        var res = await resolver.ResolveAsync("first ever coding goal");

        Assert.True(res.Created);
        Assert.False(res.Confidence.IsAssessed);   // nothing to compare against → gap
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ResolveAsync_NoEmbedding_Creates_Unassessed()
    {
        var store = new FakeStore(new[] { Ws("ws-a") },
            new() { ["ws-a"] = new Dictionary<string, float[]> { ["f1"] = new[] { 1f, 0f } } });
        var scanner = new FakeScanner();
        var root = TempRoot();
        var resolver = MakeResolver(store, new FakeRouter(Array.Empty<float>()), scanner, root); // embedding unavailable

        var res = await resolver.ResolveAsync("goal with no embedding service");

        Assert.True(res.Created);
        Assert.False(res.Confidence.IsAssessed);   // could not assess → gap, don't risk wrong reuse
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
