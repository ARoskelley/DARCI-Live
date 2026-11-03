#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Coding.Tests;

public class CodingNodeTests
{
    private sealed class FakeTaskService : ICodingTaskService
    {
        public CreateCodingTaskRequest? LastRequest;
        public Task<CodingTaskRecord> CreateTaskAsync(CreateCodingTaskRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new CodingTaskRecord
            {
                Id = "task-1",
                WorkspaceId = request.WorkspaceId,
                Prompt = request.Prompt,
            });
        }
    }

    private sealed class FakeLoop : ICodingAgentLoop
    {
        public string? StartedTaskId;
        public NodePacket? StartedWithPacket;
        public bool StartLoop(string taskId, RunCodingTaskRequest? options = null, NodePacket? rootPacket = null)
        {
            StartedTaskId = taskId;
            StartedWithPacket = rootPacket;
            return true;
        }
        public bool IsRunning(string taskId) => true;
        public Task<CodingTaskStatusResponse?> GetStatusAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult<CodingTaskStatusResponse?>(null);
    }

    private sealed class FakeResolver : IWorkContextResolver
    {
        private readonly WorkContextResolution _resolution;
        public string? AskedIntent;
        public FakeResolver(WorkContextResolution resolution) => _resolution = resolution;
        public Task<WorkContextResolution> ResolveAsync(string intent, CancellationToken ct = default)
        {
            AskedIntent = intent;
            return Task.FromResult(_resolution);
        }
    }

    private static CodingNode MakeNode(FakeTaskService ts, FakeLoop loop, IWorkContextResolver? resolver = null) =>
        new(ts, new Lazy<ICodingAgentLoop>(() => loop), NullLogger<CodingNode>.Instance, resolver);

    [Fact]
    public async Task MissingWorkspace_NoResolver_FailsPacket()
    {
        var ts = new FakeTaskService();
        var loop = new FakeLoop();
        var node = MakeNode(ts, loop);   // no resolver wired

        var packet = NodePacket.Create("implement something", capability: Capability.WriteCode)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");

        var result = await node.HandleAsync(packet);

        Assert.Equal(NodeState.Failed, result.State);
        Assert.Null(ts.LastRequest);          // no task created
        Assert.Null(loop.StartedTaskId);      // loop never started
    }

    [Fact]
    public async Task MissingWorkspace_WithResolver_ResolvesLogsAndProceeds()
    {
        var ts = new FakeTaskService();
        var loop = new FakeLoop();
        var resolver = new FakeResolver(new WorkContextResolution(
            ContextId: "ws-new", Created: true, Confidence: Confidence.Of(0.2),
            Reasoning: "Best existing match 0.20 below reuse threshold; created a fresh workspace."));
        var node = MakeNode(ts, loop, resolver);

        var packet = NodePacket.Create("implement a Damm checksum", capability: Capability.WriteCode)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");

        var result = await node.HandleAsync(packet);

        // Resolver consulted with the intent; chosen workspace used for the task + slot.
        Assert.Equal("implement a Damm checksum", resolver.AskedIntent);
        Assert.Equal("ws-new", ts.LastRequest!.WorkspaceId);
        Assert.Equal("ws-new", result.Payload.Slot(PacketSlots.WorkspaceId));
        Assert.Equal(NodeState.Working, result.State);

        // The selection decision landed in the packet log: a Working entry with the reasoning,
        // the match confidence, and the workspace recorded as an artifact.
        var entry = result.Log.Last(e => e.Decision.Contains("Workspace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("created", entry.Decision, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0.2, entry.Confidence.Score, 5);
        Assert.Contains("ws-new", entry.Artifacts);
    }

    [Fact]
    public async Task MissingWorkspace_ResolverReturnsEmpty_FailsPacket()
    {
        var ts = new FakeTaskService();
        var loop = new FakeLoop();
        var resolver = new FakeResolver(new WorkContextResolution("", false, Confidence.Unassessed, "could not resolve"));
        var node = MakeNode(ts, loop, resolver);

        var packet = NodePacket.Create("do coding", capability: Capability.WriteCode)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");

        var result = await node.HandleAsync(packet);

        Assert.Equal(NodeState.Failed, result.State);
        Assert.Null(ts.LastRequest);
    }

    [Fact]
    public async Task WithWorkspace_CreatesTask_StartsLoop_BoundToPacket()
    {
        var ts = new FakeTaskService();
        var loop = new FakeLoop();
        var node = MakeNode(ts, loop);

        var packet = NodePacket.Create("implement Levenshtein", successCriteria: "tests pass",
                capability: Capability.WriteCode,
                slots: new Dictionary<string, string> { [PacketSlots.WorkspaceId] = "ws-42" })
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");

        var result = await node.HandleAsync(packet);

        // Task created from the packet intent + workspace.
        Assert.NotNull(ts.LastRequest);
        Assert.Equal("ws-42", ts.LastRequest!.WorkspaceId);
        Assert.Equal("implement Levenshtein", ts.LastRequest.Prompt);

        // Loop started, bound to THIS packet (same id), which carries the task id slot.
        Assert.Equal("task-1", loop.StartedTaskId);
        Assert.NotNull(loop.StartedWithPacket);
        Assert.Equal(result.Id, loop.StartedWithPacket!.Id);
        Assert.Equal("task-1", result.Payload.Slot(PacketSlots.CodingTaskId));
        Assert.Equal(NodeState.Working, result.State);   // non-blocking: returned in Working
    }
}
