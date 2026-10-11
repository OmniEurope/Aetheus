// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>A usable SMTP sender. No authentication is attempted when <see cref="Username"/> is empty.</summary>
public sealed record EmailSenderConfiguration(
    string Host,
    int Port,
    SmtpTlsMode TlsMode,
    string Username,
    string Password,
    string FromAddress)
{
    /// <summary>Keeps the password out of logs and exception messages.</summary>
    public override string ToString() =>
        $"EmailSenderConfiguration {{ Host = {Host}, Port = {Port}, TlsMode = {TlsMode}, Username = {Username}, FromAddress = {FromAddress} }}";
}
