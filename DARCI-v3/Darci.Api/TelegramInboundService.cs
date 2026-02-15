using System.Text.Json;
using Darci.Core;
using Darci.Shared;
using Darci.Tools.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Darci.Api;

public class TelegramInboundService : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly DarciNotificationPreferences _prefs;
    private readonly Awareness _awareness;
    private readonly ILogger<TelegramInboundService> _logger;
    private long _lastUpdateId;

    public TelegramInboundService(
        IHttpClientFactory httpFactory,
        DarciNotificationPreferences prefs,
        Awareness awareness,
        ILogger<TelegramInboundService> logger)
    {
        _httpFactory = httpFactory;
        _prefs = prefs;
        _awareness = awareness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Telegram inbound bootstrap: enabled={Enabled}, botTokenSet={TokenSet}, chatId={ChatId}, userId={UserId}",
            _prefs.TelegramInboundEnabled,
            !string.IsNullOrWhiteSpace(_prefs.TelegramBotToken),
            _prefs.TelegramChatId,
            _prefs.TelegramInboundUserId);

        if (!_prefs.TelegramInboundEnabled
            || string.IsNullOrWhiteSpace(_prefs.TelegramBotToken)
            || string.IsNullOrWhiteSpace(_prefs.TelegramChatId))
        {
            _logger.LogInformation("Telegram inbound is disabled or not configured.");
            return;
        }

        _logger.LogInformation("Telegram inbound listener started for chat {ChatId}", _prefs.TelegramChatId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnce(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram inbound poll failed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task PollOnce(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        var url =
            $"https://api.telegram.org/bot{_prefs.TelegramBotToken}/getUpdates?timeout=30&offset={_lastUpdateId + 1}";

        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
            return;
        if (!doc.RootElement.TryGetProperty("result", out var resultEl)
            || resultEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var update in resultEl.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var updateIdEl))
            {
                _lastUpdateId = Math.Max(_lastUpdateId, updateIdEl.GetInt64());
            }

            if (!update.TryGetProperty("message", out var messageEl))
                continue;
            if (!messageEl.TryGetProperty("chat", out var chatEl))
                continue;
            if (!chatEl.TryGetProperty("id", out var chatIdEl))
                continue;

            var incomingChatId = chatIdEl.ToString();
            if (!string.Equals(incomingChatId, _prefs.TelegramChatId, StringComparison.Ordinal))
                continue;

            if (messageEl.TryGetProperty("from", out var fromEl)
                && fromEl.TryGetProperty("is_bot", out var isBotEl)
                && isBotEl.GetBoolean())
            {
                continue;
            }

            if (!messageEl.TryGetProperty("text", out var textEl))
                continue;

            var text = textEl.GetString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var incoming = new IncomingMessage
            {
                Content = text.Trim(),
                UserId = _prefs.TelegramInboundUserId,
                Source = "telegram",
                Urgency = Urgency.Soon,
                ReceivedAt = DateTime.UtcNow
            };

            await _awareness.NotifyMessage(incoming);
            _logger.LogInformation("Accepted Telegram inbound message for DARCI from chat {ChatId}", incomingChatId);
        }

        _logger.LogDebug("Telegram inbound poll completed; offset={Offset}, updates={Count}", _lastUpdateId, resultEl.GetArrayLength());
    }
}
