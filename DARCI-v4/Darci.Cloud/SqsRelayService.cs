using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Darci.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Cloud;

/// <summary>
/// Background service that bridges DARCI's message pipeline with AWS SQS.
///
/// Inbox  (app → DARCI):   polls <see cref="CloudConfig.InboxQueueUrl"/> and forwards
///                          each message into <see cref="IMessageInbox"/>.
///
/// Outbox (DARCI → app):   consumes <see cref="IMessageOutbox"/> and publishes each
///                          outgoing message to <see cref="CloudConfig.OutboxQueueUrl"/>.
///
/// When AWS credentials are not configured the service logs a warning and exits gracefully —
/// DARCI continues running normally using the local API / Telegram channels.
/// </summary>
public sealed class SqsRelayService : BackgroundService
{
    private readonly CloudConfig _config;
    private readonly IMessageInbox _inbox;
    private readonly IMessageOutbox _outbox;
    private readonly ILogger<SqsRelayService> _logger;
    private AmazonSQSClient? _sqs;

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public SqsRelayService(
        CloudConfig config,
        IMessageInbox inbox,
        IMessageOutbox outbox,
        ILogger<SqsRelayService>? logger = null)
    {
        _config = config;
        _inbox  = inbox;
        _outbox = outbox;
        _logger = logger ?? NullLogger<SqsRelayService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_config.IsConfigured)
        {
            _logger.LogWarning(
                "AWS cloud relay is not configured. " +
                "Set DARCI_AWS_KEY_ID, DARCI_AWS_KEY_SECRET, DARCI_SQS_INBOX, " +
                "DARCI_SQS_OUTBOX, and DARCI_S3_BUCKET in .env.local to enable it.");
            return;
        }

        _sqs = new AmazonSQSClient(
            new BasicAWSCredentials(_config.AccessKeyId, _config.SecretAccessKey),
            RegionEndpoint.GetBySystemName(_config.Region));

        _logger.LogInformation("SQS relay active. Inbox: {Inbox}", _config.InboxQueueUrl);

        // Run inbox polling and outbox forwarding concurrently
        await Task.WhenAll(
            PollInboxAsync(ct),
            ForwardOutboxAsync(ct));
    }

    // ─── Inbox: SQS → DARCI ──────────────────────────────────────────────────

    private async Task PollInboxAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _sqs!.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl            = _config.InboxQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds     = _config.LongPollSeconds  // long-poll — up to 20s
                }, ct);

                foreach (var sqsMsg in result.Messages)
                {
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<SqsMessageEnvelope>(sqsMsg.Body, _json);
                        if (envelope is null) continue;

                        var incoming = new IncomingMessage
                        {
                            Content    = envelope.Content,
                            UserId     = envelope.UserId ?? "Tinman",
                            Source     = "aws-sqs",
                            Urgency    = envelope.Urgent ? Urgency.Now : Urgency.Soon,
                            ReceivedAt = DateTime.UtcNow
                        };

                        await _inbox.EnqueueAsync(incoming, ct);
                        _logger.LogDebug("SQS → DARCI: {Preview}",
                            envelope.Content.Length > 80 ? envelope.Content[..80] + "…" : envelope.Content);

                        // Delete the message — we've consumed it
                        await _sqs.DeleteMessageAsync(
                            _config.InboxQueueUrl, sqsMsg.ReceiptHandle, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process SQS inbox message {MessageId}", sqsMsg.MessageId);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SQS inbox poll error — retrying in {Ms}ms", _config.PollIntervalMs);
                await Task.Delay(_config.PollIntervalMs, ct);
            }
        }
    }

    // ─── Outbox: DARCI → SQS ─────────────────────────────────────────────────

    private async Task ForwardOutboxAsync(CancellationToken ct)
    {
        await foreach (var outgoing in _outbox.ReadAllAsync(ct))
        {
            try
            {
                var envelope = new SqsResponseEnvelope
                {
                    UserId    = outgoing.UserId,
                    Content   = outgoing.Content,
                    CreatedAt = outgoing.CreatedAt.ToString("O")
                };

                await _sqs!.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl    = _config.OutboxQueueUrl,
                    MessageBody = JsonSerializer.Serialize(envelope, _json)
                }, ct);

                _logger.LogDebug("DARCI → SQS: {Preview}",
                    outgoing.Content.Length > 80 ? outgoing.Content[..80] + "…" : outgoing.Content);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to forward DARCI response to SQS outbox");
            }
        }
    }
}

// ─── Wire-format DTOs ─────────────────────────────────────────────────────────

/// <summary>App → SQS → DARCI. Matches the Android app's SQS message format.</summary>
public sealed class SqsMessageEnvelope
{
    public string Content { get; init; } = "";
    public string? UserId { get; init; }
    public bool Urgent { get; init; }
}

/// <summary>DARCI → SQS → App. Matches the Android app's expected response format.</summary>
public sealed class SqsResponseEnvelope
{
    public string UserId    { get; init; } = "";
    public string Content   { get; init; } = "";
    public string CreatedAt { get; init; } = "";
}
