namespace Enactive.Tools;

using Enactive.Core.Mail;
using MailKit.Net.Smtp;
using MimeKit;

/// <summary>
/// Sending one message through the configured mail account: connect as the account says (SendEmailTool.SocketOptions),
/// sign in when it has a user, send, leave. Shared by the send_email tool and the service's notices, so the two cannot
/// disagree about how a server is spoken to - a STARTTLS rule fixed in one and not the other is a notice that never
/// arrives while the tool works.
/// </summary>
public static class MailSending
{
    /// <exception cref="Exception">The server's own refusal - a wrong password, a blocked port, a sender it will not relay.</exception>
    public static async Task SendAsync(MailAccount account, MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = SendEmailTool.TimeoutSeconds * 1000 };
        await client.ConnectAsync(account.Host, account.Port, SendEmailTool.SocketOptions(account), ct);

        // A server that wants no credentials is a real configuration - a relay on a LAN, a local submission agent -
        // and offering it an empty password is how that fails.
        if (!string.IsNullOrWhiteSpace(account.User))
            await client.AuthenticateAsync(account.User, account.Password, ct);

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
