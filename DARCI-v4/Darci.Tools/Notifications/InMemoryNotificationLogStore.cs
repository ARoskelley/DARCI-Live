using System.Collections.Concurrent;

namespace Darci.Tools.Notifications;

public class InMemoryNotificationLogStore : INotificationLogStore
{
    private readonly ConcurrentQueue<NotificationLogEntry> _entries = new();
    private const int MaxEntries = 500;

    public void Add(NotificationLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
        {
            _entries.TryDequeue(out _);
        }
    }

    public IReadOnlyList<NotificationLogEntry> GetRecent(int limit)
    {
        limit = Math.Clamp(limit, 1, MaxEntries);
        return _entries.Reverse().Take(limit).ToList();
    }
}
