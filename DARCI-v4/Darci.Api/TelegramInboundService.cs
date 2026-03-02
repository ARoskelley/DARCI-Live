using System.Text.Json;
using System.Text;
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
    private PendingCollectionPayload? _pendingCollectionPayload;

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

            var normalizedText = text.Trim();
            if (TryAssembleCollectionPayload(normalizedText, out var assembledPayload))
            {
                if (assembledPayload == null)
                {
                    continue;
                }

                normalizedText = assembledPayload;
            }

            var incoming = new IncomingMessage
            {
                Content = normalizedText,
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

    private bool TryAssembleCollectionPayload(string incomingText, out string? assembledPayload)
    {
        assembledPayload = null;
        ClearStaleCollectionBuffer();

        if (string.IsNullOrWhiteSpace(incomingText))
        {
            return false;
        }

        var text = incomingText.Trim();
        if (_pendingCollectionPayload != null)
        {
            if (LooksLikeCollectionContinuation(text) || HasCollectionTag(text) || LooksLikeCollectionJson(text))
            {
                _pendingCollectionPayload.Append(text);
                if (HasBalancedJsonObject(_pendingCollectionPayload.Content))
                {
                    assembledPayload = EnsureCollectionTag(_pendingCollectionPayload.Content);
                    _logger.LogInformation(
                        "Assembled engineering collection payload from {ChunkCount} Telegram chunks ({Length} chars).",
                        _pendingCollectionPayload.ChunkCount,
                        assembledPayload.Length);
                    _pendingCollectionPayload = null;
                }
                else
                {
                    _logger.LogInformation(
                        "Buffered Telegram collection chunk {ChunkCount}; waiting for remaining JSON payload.",
                        _pendingCollectionPayload.ChunkCount);
                }

                return true;
            }

            // Keep the partial buffer alive for a short time, and let unrelated messages pass through.
            return false;
        }

        var hasTag = HasCollectionTag(text);
        var looksJson = LooksLikeCollectionJson(text);

        // Tagged plain-language requests should flow through directly.
        if (hasTag && !text.Contains('{') && !looksJson)
        {
            return false;
        }

        if (!hasTag && !looksJson)
        {
            return false;
        }

        if (HasBalancedJsonObject(text))
        {
            assembledPayload = EnsureCollectionTag(text);
            return true;
        }

        _pendingCollectionPayload = new PendingCollectionPayload(text);
        _logger.LogInformation("Started buffering Telegram collection payload chunk 1.");
        return true;
    }

    private void ClearStaleCollectionBuffer()
    {
        if (_pendingCollectionPayload == null)
        {
            return;
        }

        var age = DateTime.UtcNow - _pendingCollectionPayload.LastChunkAtUtc;
        if (age <= TimeSpan.FromMinutes(3))
        {
            return;
        }

        _logger.LogWarning(
            "Discarding stale partial Telegram collection payload after {AgeSeconds}s and {ChunkCount} chunks.",
            age.TotalSeconds,
            _pendingCollectionPayload.ChunkCount);
        _pendingCollectionPayload = null;
    }

    private static bool HasCollectionTag(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        return lower.StartsWith("#collection", StringComparison.Ordinal)
            || lower.StartsWith("/collection", StringComparison.Ordinal)
            || lower.StartsWith("#assembly", StringComparison.Ordinal)
            || lower.StartsWith("/assembly", StringComparison.Ordinal);
    }

    private static bool LooksLikeCollectionJson(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.Contains("\"parts\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\"connections\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\"name\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCollectionContinuation(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal)
            || trimmed.StartsWith("}", StringComparison.Ordinal)
            || trimmed.StartsWith("[", StringComparison.Ordinal)
            || trimmed.StartsWith("]", StringComparison.Ordinal)
            || trimmed.StartsWith("\"", StringComparison.Ordinal)
            || trimmed.StartsWith(",", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.Contains('{')
            || trimmed.Contains('}')
            || trimmed.Contains('[')
            || trimmed.Contains(']')
            || trimmed.Contains("\":", StringComparison.Ordinal)
            || trimmed.Contains("\"parts\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\"connections\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\"motion\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\"parameters\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasBalancedJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        var sawOpen = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                sawOpen = true;
                depth++;
                continue;
            }

            if (ch == '}')
            {
                depth--;
                if (depth == 0 && sawOpen)
                {
                    return true;
                }

                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static string EnsureCollectionTag(string text)
    {
        var trimmed = text.Trim();
        return HasCollectionTag(trimmed) ? trimmed : $"#collection\n{trimmed}";
    }

    private sealed class PendingCollectionPayload
    {
        private readonly StringBuilder _builder = new();

        public PendingCollectionPayload(string firstChunk)
        {
            Append(firstChunk);
        }

        public DateTime LastChunkAtUtc { get; private set; }

        public int ChunkCount { get; private set; }

        public string Content => _builder.ToString();

        public void Append(string chunk)
        {
            if (_builder.Length > 0)
            {
                _builder.AppendLine();
            }

            _builder.Append(chunk.Trim());
            LastChunkAtUtc = DateTime.UtcNow;
            ChunkCount++;
        }
    }
}
