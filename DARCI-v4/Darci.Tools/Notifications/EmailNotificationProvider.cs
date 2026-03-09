using Darci.Shared;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Mail;

namespace Darci.Tools.Notifications;

public class EmailNotificationProvider : INotificationProvider
{
    private readonly ILogger<EmailNotificationProvider> _logger;

    public EmailNotificationProvider(ILogger<EmailNotificationProvider> logger)
    {
        _logger = logger;
    }

    public string Name => "email";

    public bool IsEnabled(DarciNotificationPreferences preferences) =>
        preferences.EmailEnabled
        && !string.IsNullOrWhiteSpace(preferences.EmailTo)
        && !string.IsNullOrWhiteSpace(preferences.EmailFrom)
        && !string.IsNullOrWhiteSpace(preferences.SmtpHost);

    public string GetTarget(DarciNotificationPreferences preferences) => preferences.EmailTo;

    public async Task<string?> SendAsync(
        OutgoingMessage message,
        DarciNotificationPreferences preferences,
        CancellationToken ct)
    {
        using var smtp = new SmtpClient(preferences.SmtpHost, preferences.SmtpPort)
        {
            EnableSsl = preferences.SmtpUseSsl
        };

        if (!string.IsNullOrWhiteSpace(preferences.SmtpUsername))
        {
            smtp.Credentials = new NetworkCredential(
                preferences.SmtpUsername,
                preferences.SmtpPassword);
        }

        using var mail = new MailMessage(
            from: preferences.EmailFrom,
            to: preferences.EmailTo,
            subject: "DARCI Notification",
            body: message.Content);

        _logger.LogInformation("Sending EMAIL notification to {Email}", preferences.EmailTo);
        await smtp.SendMailAsync(mail, ct);
        return Guid.NewGuid().ToString("N");
    }
}
