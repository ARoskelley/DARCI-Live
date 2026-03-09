using System.Text.Json;
using Darci.Shared;
using Microsoft.Extensions.Logging;

namespace Darci.Tools.Notifications;

public class TelegramNotificationProvider : INotificationProvider
{
    private readonly ILogger<TelegramNotificationProvider> _logger;
    private readonly IHttpClientFactory _httpFactory;

    public TelegramNotificationProvider(
        ILogger<TelegramNotificationProvider> logger,
        IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _httpFactory = httpFactory;
    }

    public string Name => "telegram";

    public bool IsEnabled(DarciNotificationPreferences preferences) =>
        preferences.TelegramEnabled
        && !string.IsNullOrWhiteSpace(preferences.TelegramBotToken)
        && !string.IsNullOrWhiteSpace(preferences.TelegramChatId);

    public string GetTarget(DarciNotificationPreferences preferences) => preferences.TelegramChatId;

    public async Task<string?> SendAsync(
        OutgoingMessage message,
        DarciNotificationPreferences preferences,
        CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        var url = $"https://api.telegram.org/bot{preferences.TelegramBotToken}/sendMessage";

        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = preferences.TelegramChatId,
            ["text"] = Truncate(message.Content, 3900)
        });

        _logger.LogInformation("Sending Telegram notification to chat {ChatId}", preferences.TelegramChatId);
        var response = await client.PostAsync(url, body, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("message_id", out var id))
            {
                return id.ToString();
            }
        }
        catch
        {
            // Ignore parse failure; send still succeeded.
        }

        return null;
    }

    private static string Truncate(string content, int maxChars) =>
        content.Length <= maxChars ? content : content[..maxChars] + "...";
}
