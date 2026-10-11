// SPDX-License-Identifier: EUPL-1.2
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Aetheus.Back.Services.Email;

/// <summary>
/// MailKit SMTP client. The server certificate is validated by the platform's default rules: a
/// self-signed or mismatched certificate fails the connection, and its error is reported as is.
/// </summary>
public sealed class MailKitSmtpTransport : ISmtpTransport
{
    private const int TimeoutMilliseconds = 20_000;
    private const string SenderDisplayName = "Aetheus";

    public async Task<ISmtpSession> ConnectAsync(EmailSenderConfiguration configuration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var client = new SmtpClient { Timeout = TimeoutMilliseconds };
        try
        {
            await client.ConnectAsync(configuration.Host, configuration.Port, ToSocketOptions(configuration.TlsMode), ct)
                .ConfigureAwait(false);
            if (configuration.Username.Length > 0)
                await client.AuthenticateAsync(configuration.Username, configuration.Password, ct).ConfigureAwait(false);
            return new Session(client, new MailboxAddress(SenderDisplayName, configuration.FromAddress));
        }
        catch
        {
            // Mandatory: a client that failed half-way must not leak its socket; the error goes on up.
            client.Dispose();
            throw;
        }
    }

    private static SecureSocketOptions ToSocketOptions(SmtpTlsMode mode) => mode switch
    {
        SmtpTlsMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpTlsMode.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls
    };

    private sealed class Session(SmtpClient client, MailboxAddress from) : ISmtpSession
    {
        public async Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(message);
            var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
            using var mime = new MimeMessage
            {
                Subject = message.Subject,
                Body = body.ToMessageBody()
            };
            mime.From.Add(from);
            mime.To.Add(MailboxAddress.Parse(message.To));
            await client.SendAsync(mime, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(quit: true).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or MailKit.ProtocolException or SmtpCommandException)
            {
                // Mandatory: the messages are already accepted or refused; a failed QUIT changes neither.
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
