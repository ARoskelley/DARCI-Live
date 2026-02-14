namespace Darci.Tools.Notifications;

public class DarciNotificationPreferences
{
    public bool EmailEnabled { get; init; } = true;
    public bool TelegramEnabled { get; init; } = true;

    public string EmailTo { get; init; } = "";
    public string EmailFrom { get; init; } = "";
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 587;
    public bool SmtpUseSsl { get; init; } = true;
    public string SmtpUsername { get; init; } = "";
    public string SmtpPassword { get; init; } = "";

    public string TelegramBotToken { get; init; } = "";
    public string TelegramChatId { get; init; } = "";
}
