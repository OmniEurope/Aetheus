// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Mail.Mailbox;
using Aetheus.Back.Components.Settings;
using Aetheus.Back.Services.Email;

namespace Aetheus.Back.Components.Mail;

/// <summary>An administrator can generate a default sender password, apply it through a
/// mail-management task and store it encrypted because mailbox passwords cannot be recovered from
/// server hashes. This action never runs automatically.</summary>
public sealed class MailNoReplySenderService(
    IEmailSenderConfigurationResolver resolver,
    IMailService mailService,
    ISettingsService settings,
    IEmailSenderSettingsService senderSettings) : IMailNoReplySenderService
{
    private const int NoReplyQuotaMb = 100;

    public async Task<EmailSenderSettingsDto> PrepareAsync(CancellationToken ct = default)
    {
        var resolution = await resolver.ResolveAsync(ct).ConfigureAwait(false);
        if (resolution.NoReplyServerId is not int serverId || resolution.NoReplyAddress is not { } address)
            throw new BadRequestException("No mail domain is managed by Aetheus: there is no no-reply mailbox to prepare.");

        var password = MailPasswordGenerator.NewPassword();
        if (resolution.NoReplyAccountId is int accountId)
        {
            await mailService.UpdateAccountAsync(serverId, accountId,
                new UpdateMailAccountRequest { NewPassword = password }, ct).ConfigureAwait(false);
        }
        else if (await FindKeptRowAsync(serverId, address, ct).ConfigureAwait(false) is { } kept)
        {
            // Disabled by hand but still on the server: the mailbox exists, so it gets the new password
            // and is switched back on (a disabled sender cannot be resolved).
            await mailService.UpdateAccountAsync(serverId, kept.Id,
                new UpdateMailAccountRequest { NewPassword = password, IsActive = true }, ct).ConfigureAwait(false);
        }
        else
        {
            // No row, or a row the reconciliation flagged absent from the server: creating the mailbox
            // again records it on that same row (see MailService.CreateAccountAsync).
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

    /// <summary>The row holding the address when its mailbox is still on the server, whatever its flag. The
    /// resolver only reports an active, present row, so a disabled one has to be looked up here.</summary>
    private async Task<MailAccountDto?> FindKeptRowAsync(int serverId, string address, CancellationToken ct)
    {
        var accounts = await mailService.GetAccountsAsync(serverId, ct).ConfigureAwait(false);
        return accounts.FirstOrDefault(account =>
            account.MissingSince is null && string.Equals(account.Email, address, StringComparison.OrdinalIgnoreCase));
    }
}
