using System.Threading.Channels;
using Darci.Shared;

namespace Darci.Tools.Notifications;

public class InMemoryResponseStore : IResponseStore
{
    private readonly Channel<OutgoingMessage> _messages = Channel.CreateUnbounded<OutgoingMessage>();

    public async ValueTask AddAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        await _messages.Writer.WriteAsync(message, ct);
    }

    public bool TryRead(out OutgoingMessage message)
    {
        return _messages.Reader.TryRead(out message!);
    }

    public ValueTask<OutgoingMessage> ReadAsync(CancellationToken ct)
    {
        return _messages.Reader.ReadAsync(ct);
    }
}
