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

    private static CodingNode MakeNode(FakeTaskService ts, FakeLoop loop) =>
        new(ts, new Lazy<ICodingAgentLoop>(() => loop), NullLogger<CodingNode>.Instance);

    [Fact]
    public async Task MissingWorkspace_FailsPacket_WithoutCreatingTask()
    {
        var ts = new FakeTaskService();
        var loop = new FakeLoop();
        var node = MakeNode(ts, loop);

        var packet = NodePacket.Create("implement something", capability: Capability.WriteCode)
            .Transition(NodeId.Orchestrator, NodeState.Routed, "routed");

        var result = await node.HandleAsync(packet);

        Assert.Equal(NodeState.Failed, result.State);
        Assert.Null(ts.LastRequest);          // no task created
        Assert.Null(loop.StartedTaskId);      // loop never started
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
