#nullable enable

using Darci.Shared;

namespace Darci.Research.Agents.Tests.Contract;

/// <summary>
/// D5 — the correlation handle that lets a client match a reply to the message it sent.
///
/// <para>This mattered because the hub broadcasts to <c>Clients.All</c>. With a phone and a desktop app
/// connected at once, both receive every reply, and with every message arriving as id 0 neither could tell
/// which answer was its own or clear a pending state on the right message.</para>
///
/// <para>The chain was broken in THREE places, which is why it never worked despite the plumbing existing:
/// <c>IncomingMessage.Id</c> was never assigned, <c>Toolkit.SendMessage</c> dropped the id the decision
/// layer had already worked out, and the hub payload omitted the field entirely. Fixing any one alone
/// would have changed nothing observable.</para>
/// </summary>
public sealed class MessageCorrelationTests
{
    [Fact]
    public void IdsStartAtOne_SoZeroKeepsMeaningNotSet()
    {
        // Every stored message and every DTO already treats 0 as "no correlation". Starting the sequence
        // at 0 would make the first real message indistinguishable from an uncorrelated one.
        Assert.Equal(1, new MessageIdSequence().Next());
    }

    [Fact]
    public void IdsAreMonotonicAndUnique()
    {
        var sequence = new MessageIdSequence();
        var ids = Enumerable.Range(0, 100).Select(_ => sequence.Next()).ToList();

        Assert.Equal(ids.OrderBy(i => i), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void IdsAreUniqueUnderConcurrentIngress()
    {
        // REST and the hub are separate ingress paths sharing one sequence, and both can be hit at once.
        // A duplicate id would silently mis-correlate one client's reply onto another's message.
        var sequence = new MessageIdSequence();
        var ids = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 1000, _ => ids.Add(sequence.Next()));

        Assert.Equal(1000, ids.Distinct().Count());
    }

    [Fact]
    public void AnIncomingMessage_CarriesTheAssignedId()
    {
        var sequence = new MessageIdSequence();
        var message = new IncomingMessage { Id = sequence.Next(), Content = "hello" };

        Assert.NotEqual(0, message.Id);
    }

    [Fact]
    public void AnOutgoingMessage_CanPointBackAtTheMessageItAnswers()
    {
        var reply = new OutgoingMessage { UserId = "Tinman", Content = "hi", InResponseToMessageId = 7 };

        Assert.Equal(7, reply.InResponseToMessageId);
    }

    [Fact]
    public void AnUnpromptedMessage_HasNoCorrelation_AndThatIsHonest()
    {
        // Notifications and proactive nudges answer nothing. Inventing a correlation for them would make
        // a client clear a pending state on a message that was never actually answered.
        var notification = new OutgoingMessage { UserId = "Tinman", Content = "your build finished" };

        Assert.Null(notification.InResponseToMessageId);
    }
}
