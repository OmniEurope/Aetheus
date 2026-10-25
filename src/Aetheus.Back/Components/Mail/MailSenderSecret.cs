// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Mail;

/// <summary>Keeps the password the default sender reads in step with the one set on the mail server. The sender
/// logs in as the <c>no-reply</c> mailbox with the secret stored under that address; a password set on that
/// mailbox through any Aetheus operation (create, change, setup) must reach the secret in the same operation, or
/// the mail server refuses the sender (SMTP 535) while Aetheus keeps the old value.</summary>
public interface IMailSenderSecret
{
    /// <summary>Stores <paramref name="password"/> as the sender's password when <paramref name="email"/> is the
    /// no-reply address of its domain; does nothing for any other mailbox.</summary>
    Task StoreIfSenderAsync(string email, string password, CancellationToken ct = default);
}

public sealed class MailSenderSecret(ISettingsService settings) : IMailSenderSecret
{
    public async Task StoreIfSenderAsync(string email, string password, CancellationToken ct = default)
    {
        if (!IsSenderAddress(email) || string.IsNullOrEmpty(password))
            return;

        await settings.CreateSecretAsync(new CreateSecretRequest
        {
            Key = EmailSenderSettingKeys.NoReplyPasswordSecret(email),
            Value = password
        }, ct).ConfigureAwait(false);
    }

    internal static bool IsSenderAddress(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && string.Equals(email[..at], EmailSenderSettingKeys.NoReplyLocalPart, StringComparison.OrdinalIgnoreCase);
    }
}
