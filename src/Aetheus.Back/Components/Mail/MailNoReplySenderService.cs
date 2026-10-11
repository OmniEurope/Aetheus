// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Mail;

/// <summary>
/// Recette R1-16. Aetheus cannot read the password of a mailbox (the server keeps only its hash), so
/// the default sender works only with a password Aetheus chose: this generates one, sets it through the
/// usual mail-manage task (a new mailbox, or a password change of the existing one) and stores it,
/// encrypted, in the platform secret store. Never run on its own: an administrator asks for it.
/// </summary>
public sealed class MailNoReplySenderService(
    IEmailSenderConfigurationResolver resolver,
    IMailService mailService,
    ISettingsService settings,
    IEmailSenderSettingsService senderSettings) : IMailNoReplySenderService
{
    private const int GeneratedPasswordLength = 40;
    private const int NoReplyQuotaMb = 100;
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public async Task<EmailSenderSettingsDto> PrepareAsync(CancellationToken ct = default)
    {
        var resolution = await resolver.ResolveAsync(ct).ConfigureAwait(false);
        if (resolution.NoReplyServerId is not int serverId || resolution.NoReplyAddress is not { } address)
            throw new BadRequestException("No mail domain is managed by Aetheus: there is no no-reply mailbox to prepare.");

        var password = RandomNumberGenerator.GetString(PasswordAlphabet, GeneratedPasswordLength);
        if (resolution.NoReplyAccountId is int accountId)
        {
            await mailService.UpdateAccountAsync(serverId, accountId,
                new UpdateMailAccountRequest { NewPassword = password }, ct).ConfigureAwait(false);
        }
        else
        {
            await mailService.CreateAccountAsync(serverId,
                new CreateMailAccountRequest { Email = address, Password = password, QuotaMb = NoReplyQuotaMb }, ct).ConfigureAwait(false);
        }

        // Stored once the task is queued: a refused request leaves the previous secret untouched.
        await settings.CreateSecretAsync(new CreateSecretRequest
        {
            Key = EmailSenderSettingKeys.NoReplyPasswordSecret(address),
            Value = password
        }, ct).ConfigureAwait(false);
        return await senderSettings.GetAsync(ct).ConfigureAwait(false);
    }
}
