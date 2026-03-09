using Darci.Shared;

namespace Darci.Tools.Notifications;

public interface INotificationService
{
    Task NotifyAsync(OutgoingMessage message, CancellationToken ct);
}
