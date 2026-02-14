namespace Darci.Tools.Notifications;

public interface INotificationLogStore
{
    void Add(NotificationLogEntry entry);
    IReadOnlyList<NotificationLogEntry> GetRecent(int limit);
}

