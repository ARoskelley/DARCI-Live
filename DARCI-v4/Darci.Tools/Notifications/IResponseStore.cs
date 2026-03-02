using Darci.Shared;

namespace Darci.Tools.Notifications;

public interface IResponseStore
{
    ValueTask AddAsync(OutgoingMessage message, CancellationToken ct = default);
    bool TryRead(out OutgoingMessage message);
    ValueTask<OutgoingMessage> ReadAsync(CancellationToken ct);
}

