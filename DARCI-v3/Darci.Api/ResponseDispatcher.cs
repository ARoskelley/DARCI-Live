using Darci.Tools;
using Darci.Tools.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Darci.Api;

public class ResponseDispatcher : BackgroundService
{
    private readonly Toolkit _toolkit;
    private readonly IResponseStore _responseStore;
    private readonly INotificationService _notifications;
    private readonly ILogger<ResponseDispatcher> _logger;

    public ResponseDispatcher(
        Toolkit toolkit,
        IResponseStore responseStore,
        INotificationService notifications,
        ILogger<ResponseDispatcher> logger)
    {
        _toolkit = toolkit;
        _responseStore = responseStore;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _toolkit.OutgoingMessages.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _responseStore.AddAsync(message, stoppingToken);
                await _notifications.NotifyAsync(message, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to dispatch outgoing message for {UserId}", message.UserId);
            }
        }
    }
}

