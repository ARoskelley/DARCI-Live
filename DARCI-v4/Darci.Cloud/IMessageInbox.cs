using Darci.Shared;

namespace Darci.Cloud;

/// <summary>
/// Accepts incoming messages from external sources (SQS, API, etc.)
/// and delivers them to DARCI's perception pipeline.
///
/// Implemented in Darci.Api by wrapping <see cref="Awareness.NotifyMessage"/>.
/// </summary>
public interface IMessageInbox
{
    Task EnqueueAsync(IncomingMessage message, CancellationToken ct = default);
}

/// <summary>
/// Provides a stream of outgoing messages produced by DARCI.
/// The SQS relay reads from this and publishes to the outbox queue.
///
/// Implemented in Darci.Api by wrapping the existing IResponseStore channel.
/// </summary>
public interface IMessageOutbox
{
    IAsyncEnumerable<OutgoingMessage> ReadAllAsync(CancellationToken ct);
}
