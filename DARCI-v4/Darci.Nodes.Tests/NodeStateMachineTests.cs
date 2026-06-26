using Darci.Nodes;

namespace Darci.Nodes.Tests;

public class NodeStateMachineTests
{
    [Theory]
    [InlineData(NodeState.Created, NodeState.Routed, true)]
    [InlineData(NodeState.Routed, NodeState.Accepted, true)]
    [InlineData(NodeState.Accepted, NodeState.Working, true)]
    [InlineData(NodeState.Working, NodeState.AwaitingDependency, true)]
    [InlineData(NodeState.Working, NodeState.Succeeded, true)]
    [InlineData(NodeState.Working, NodeState.Failed, true)]
    [InlineData(NodeState.AwaitingDependency, NodeState.Working, true)]
    public void LegalTransitions_AreAllowed(NodeState from, NodeState to, bool expected)
    {
        Assert.Equal(expected, NodeStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(NodeState.Created, NodeState.Working)]    // can't skip routing/accept
    [InlineData(NodeState.Created, NodeState.Succeeded)]
    [InlineData(NodeState.Routed, NodeState.Working)]
    [InlineData(NodeState.Accepted, NodeState.Succeeded)]
    public void IllegalTransitions_AreRejected(NodeState from, NodeState to)
    {
        Assert.False(NodeStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(NodeState.Succeeded)]
    [InlineData(NodeState.Failed)]
    [InlineData(NodeState.Aborted)]
    public void TerminalStates_HaveNoOutgoingTransitions(NodeState terminal)
    {
        foreach (NodeState to in Enum.GetValues<NodeState>())
            Assert.False(NodeStateMachine.CanTransition(terminal, to),
                $"{terminal} → {to} must be rejected");
        Assert.True(terminal.IsTerminal());
        Assert.False(terminal.IsActive());
    }

    [Theory]
    [InlineData(NodeState.Created)]
    [InlineData(NodeState.Routed)]
    [InlineData(NodeState.Accepted)]
    [InlineData(NodeState.Working)]
    [InlineData(NodeState.AwaitingDependency)]
    public void AnyActiveState_CanAlwaysBeAborted(NodeState active)
    {
        Assert.True(active.IsActive());
        Assert.True(NodeStateMachine.CanTransition(active, NodeState.Aborted));
    }

    [Fact]
    public void Transition_OnTerminalPacket_Throws()
    {
        var packet = NodePacket.Create("test")
            .Transition(NodeId.Coding, NodeState.Routed, "route")
            .Transition(NodeId.Coding, NodeState.Accepted, "accept")
            .Transition(NodeId.Coding, NodeState.Working, "work")
            .Transition(NodeId.Coding, NodeState.Succeeded, "done");

        Assert.Throws<InvalidNodeTransitionException>(() =>
            packet.Transition(NodeId.Coding, NodeState.Working, "resurrect"));
    }
}
