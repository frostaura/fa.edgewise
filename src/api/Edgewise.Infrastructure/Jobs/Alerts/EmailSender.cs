using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Edgewise.Infrastructure.Jobs.Alerts;

/// <summary>
/// Sends alert emails via MailKit. Silently a no-op unless Smtp__Host is
/// configured (optional: Smtp__Port, Smtp__User, Smtp__Password, Smtp__From,
/// Smtp__UseSsl). Failures are logged, never thrown.
/// </summary>
public class EmailSender(IConfiguration config, ILogger<EmailSender> logger)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Smtp:Host"]);

    /// <summary>Returns true when the email was sent.</summary>
    public virtual async Task<bool> SendAsync(
        string toEmail, string subject, string body, CancellationToken ct)
    {
        var host = config["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(config["Smtp:From"] ?? "edgewise@localhost"));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            var port = int.TryParse(config["Smtp:Port"], out var p) ? p : 587;
            var useSsl = string.Equals(config["Smtp:UseSsl"], "true", StringComparison.OrdinalIgnoreCase);
            await client.ConnectAsync(
                host, port, useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto, ct);

            var user = config["Smtp:User"];
            if (!string.IsNullOrWhiteSpace(user))
            {
                await client.AuthenticateAsync(user, config["Smtp:Password"] ?? string.Empty, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Alert email delivery to {Email} failed", toEmail);
            return false;
        }
    }
}
