namespace Darci.Tools.Notifications;

public class NotificationLogEntry
{
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
    public string Provider { get; init; } = "";
    public string Status { get; init; } = "";
    public string UserId { get; init; } = "";
    public string Target { get; init; } = "";
    public string MessagePreview { get; init; } = "";
    public string? ProviderMessageId { get; init; }
    public string? Error { get; init; }
}

