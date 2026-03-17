namespace Darci.Cloud;

/// <summary>
/// AWS configuration loaded from environment variables.
/// Set these in .env.local (never commit real credentials).
/// </summary>
public sealed class CloudConfig
{
    /// <summary>AWS region, e.g. us-east-1</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>IAM access key for the darci-agent IAM user.</summary>
    public string AccessKeyId { get; init; } = "";

    /// <summary>IAM secret access key for the darci-agent IAM user.</summary>
    public string SecretAccessKey { get; init; } = "";

    /// <summary>SQS queue URL for messages from the app → DARCI.</summary>
    public string InboxQueueUrl { get; init; } = "";

    /// <summary>SQS queue URL for messages from DARCI → app.</summary>
    public string OutboxQueueUrl { get; init; } = "";

    /// <summary>S3 bucket name for research file storage.</summary>
    public string FilesBucket { get; init; } = "";

    /// <summary>How long to wait between SQS inbox polls (ms). Default 2 000.</summary>
    public int PollIntervalMs { get; init; } = 2_000;

    /// <summary>SQS long-poll wait time in seconds (0–20). Default 5.</summary>
    public int LongPollSeconds { get; init; } = 5;

    /// <summary>Pre-signed URL expiry in minutes. Default 60.</summary>
    public int PresignedUrlExpiryMinutes { get; init; } = 60;

    /// <summary>Returns true if AWS credentials + queues are configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessKeyId)  &&
        !string.IsNullOrWhiteSpace(SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(InboxQueueUrl) &&
        !string.IsNullOrWhiteSpace(OutboxQueueUrl) &&
        !string.IsNullOrWhiteSpace(FilesBucket);

    /// <summary>Load configuration from environment variables.</summary>
    public static CloudConfig FromEnvironment() => new()
    {
        Region               = Env("DARCI_AWS_REGION",     "us-east-1"),
        AccessKeyId          = Env("DARCI_AWS_KEY_ID",     ""),
        SecretAccessKey      = Env("DARCI_AWS_KEY_SECRET", ""),
        InboxQueueUrl        = Env("DARCI_SQS_INBOX",      ""),
        OutboxQueueUrl       = Env("DARCI_SQS_OUTBOX",     ""),
        FilesBucket          = Env("DARCI_S3_BUCKET",      ""),
        PollIntervalMs       = int.TryParse(Env("DARCI_CLOUD_POLL_MS", "2000"), out var p)  ? p  : 2_000,
        LongPollSeconds      = int.TryParse(Env("DARCI_CLOUD_WAIT_S",  "5"),    out var w)  ? w  : 5,
        PresignedUrlExpiryMinutes = int.TryParse(Env("DARCI_PRESIGNED_MIN", "60"), out var e) ? e : 60
    };

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;
}
