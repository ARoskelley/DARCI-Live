using Darci.Shared;

namespace Darci.Tools.Notifications;

public interface INotificationProvider
{
    string Name { get; }
    bool IsEnabled(DarciNotificationPreferences preferences);
    string GetTarget(DarciNotificationPreferences preferences);
    Task<string?> SendAsync(OutgoingMessage message, DarciNotificationPreferences preferences, CancellationToken ct);
}
