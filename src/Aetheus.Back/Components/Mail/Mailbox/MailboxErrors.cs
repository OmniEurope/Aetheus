// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Turns a MailKit failure into the API's own exceptions, keeping the server's reason: a refused login
/// becomes 409 (the backend forgets the password and the page asks for it again), an unknown folder or message 404,
/// anything that kept the server from answering 400 with what happened. Never the password.
/// </summary>
public static class MailboxErrors
{
    /// <summary>The exception to throw instead, or null to let the original go on up unchanged.</summary>
    public static Exception? Translate(Exception exception, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => null,
            AuthenticationException => new ConflictException(
                $"The mail server {host} refused the login of this mailbox: {exception.Message}"),
            FolderNotFoundException => new NotFoundException("This folder does not exist in the mailbox."),
            MessageNotFoundException => new NotFoundException("This message is no longer in the folder."),
            SslHandshakeException => new BadRequestException(
                $"The TLS connection to {host}:{port} failed (certificate refused or protocol mismatch): {exception.Message}"),
            SocketException or IOException or TimeoutException or ServiceNotConnectedException
                or ImapProtocolException or SmtpProtocolException => new BadRequestException(
                $"The mail server {host}:{port} could not be reached: {exception.Message}"),
            ImapCommandException or SmtpCommandException => new BadRequestException(
                $"The mail server {host}:{port} refused the request: {exception.Message}"),
            _ => null
        };
    }
}
