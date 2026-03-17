using Darci.Cloud;
using Darci.Core;
using Darci.Shared;
using Darci.Tools.Notifications;

namespace Darci.Api;

/// <summary>
/// Implements IMessageInbox by forwarding messages into DARCI's Awareness pipeline.
/// </summary>
public sealed class AwarenessMessageInbox : IMessageInbox
{
    private readonly Awareness _awareness;
    public AwarenessMessageInbox(Awareness awareness) => _awareness = awareness;

    public async Task EnqueueAsync(IncomingMessage message, CancellationToken ct = default) =>
        await _awareness.NotifyMessage(message);
}

/// <summary>
/// Implements IMessageOutbox by tapping into the existing IResponseStore channel.
/// Runs as a background task — yields each OutgoingMessage as it arrives.
/// </summary>
public sealed class ResponseStoreOutbox : IMessageOutbox
{
    private readonly IResponseStore _store;
    public ResponseStoreOutbox(IResponseStore store) => _store = store;

    public async IAsyncEnumerable<OutgoingMessage> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            OutgoingMessage msg;
            try
            {
                msg = await _store.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            yield return msg;
        }
    }
}
