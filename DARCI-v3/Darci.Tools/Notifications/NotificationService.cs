using Darci.Shared;
using Microsoft.Extensions.Logging;

namespace Darci.Tools.Notifications;

public class NotificationService : INotificationService
{
    private readonly IEnumerable<INotificationProvider> _providers;
    private readonly DarciNotificationPreferences _preferences;
    private readonly INotificationLogStore _logStore;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IEnumerable<INotificationProvider> providers,
        DarciNotificationPreferences preferences,
        INotificationLogStore logStore,
        ILogger<NotificationService> logger)
    {
        _providers = providers;
        _preferences = preferences;
        _logStore = logStore;
        _logger = logger;
    }

    public async Task NotifyAsync(OutgoingMessage message, CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            var target = provider.GetTarget(_preferences);
            if (!provider.IsEnabled(_preferences))
            {
                _logStore.Add(new NotificationLogEntry
                {
                    Provider = provider.Name,
                    Status = "skipped",
                    UserId = message.UserId,
                    Target = target,
                    MessagePreview = Truncate(message.Content, 200),
                    Error = "Provider disabled or not configured"
                });
                continue;
            }

            try
            {
                var providerMessageId = await provider.SendAsync(message, _preferences, ct);
                _logStore.Add(new NotificationLogEntry
                {
                    Provider = provider.Name,
                    Status = "sent",
                    UserId = message.UserId,
                    Target = target,
                    MessagePreview = Truncate(message.Content, 200),
                    ProviderMessageId = providerMessageId
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notification provider {Provider} failed", provider.Name);
                _logStore.Add(new NotificationLogEntry
                {
                    Provider = provider.Name,
                    Status = "failed",
                    UserId = message.UserId,
                    Target = target,
                    MessagePreview = Truncate(message.Content, 200),
                    Error = ex.Message
                });
            }
        }
    }

    private static string Truncate(string content, int maxChars) =>
        content.Length <= maxChars ? content : content[..maxChars] + "...";
}
