namespace Darci.Shared;

/// <summary>
/// Hands out the ids that let a client tell which reply answers which message.
///
/// <para><see cref="IncomingMessage.Id"/> was declared but never assigned, so every message arrived as id
/// 0 and every reply pointed back at nothing. That was survivable with one client, but the hub broadcasts
/// to <c>Clients.All</c>: with a phone and a desktop app connected at once, each receives the other's
/// replies and — with no id — cannot tell them apart, or attach a pending state to the message it sent.</para>
///
/// <para>Process-lifetime and monotonic, which is all the contract needs. An id is a correlation handle
/// for a LIVE conversation, never a durable key: the evidence loop is keyed on the correlation root
/// (<c>GoalId</c>), and nothing here should ever be used in its place.</para>
/// </summary>
public sealed class MessageIdSequence
{
    private int _last;

    /// <summary>The next id. Starts at 1, so 0 keeps its established meaning of "not set".</summary>
    public int Next() => Interlocked.Increment(ref _last);
}
