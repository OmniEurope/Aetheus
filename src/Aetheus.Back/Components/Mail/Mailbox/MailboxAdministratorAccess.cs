// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Recette R1-41 / R3-50 (decision of 2026-10-12): an administrator opens a mailbox without its password,
/// through the Dovecot master user Aetheus generated for the server. Reserved to administrators (the caller
/// says whether the reader is one); every opening is written to the audit log with the mailbox address.
/// The master password is read from the platform secret store, checked by a real IMAP login, then held in
/// memory like a typed password (same expiry, "Verrouiller" and session end forget it). It is never
/// returned, logged or audited.
/// </summary>
public interface IMailboxAdministratorAccess
{
    /// <summary>True when the server has an administrator access prepared (a master password is stored).</summary>
    Task<bool> IsPreparedAsync(int serverId, CancellationToken ct = default);

    /// <summary>
    /// Opens the mailbox as the master user. Refused with <see cref="BadRequestException"/> for a reader who is
    /// not an administrator (the controller already answers 403 before), when no access is prepared or the server refuses it.
    /// </summary>
    Task<MailboxAccessDto> OpenAsync(MailboxCredentialKey key, bool isAdministrator, CancellationToken ct = default);
}

public sealed class MailboxAdministratorAccess(
    IMailboxRepository repository,
    IMailboxConnector connector,
    IMailboxCredentialStore credentials,
    IMailMasterSecrets secrets,
    IAuditService audit) : IMailboxAdministratorAccess
{
    public async Task<bool> IsPreparedAsync(int serverId, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(await repository.GetEncryptedSecretAsync(MailMasterAccess.SecretKey(serverId), ct).ConfigureAwait(false));

    public async Task<MailboxAccessDto> OpenAsync(MailboxCredentialKey key, bool isAdministrator, CancellationToken ct = default)
    {
        if (!isAdministrator)
            throw new BadRequestException("Only an administrator opens a mailbox without its password.");
        var account = await repository.FindAccountAsync(key.ServerId, key.AccountId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("This mailbox does not exist on the server, or is disabled.");
        if (string.IsNullOrWhiteSpace(account.Host))
            throw new BadRequestException("The server has no host name or address to reach its mail service.");
        var master = await secrets.ReadAsync(key.ServerId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(master))
            throw new BadRequestException(
                "No administrator access is prepared on this server: run \"Prepare administrator access\" on its mail page.");

        var credential = new MailboxHeldCredential(master, MailMasterAccess.Login(account.Email));
        try
        {
            await using (await connector.OpenAsync(MailboxService.Endpoint(account, credential), ct).ConfigureAwait(false))
            {
            }
        }
        catch (ConflictException)
        {
            // Mandatory: a refused master login is not "type the password again" (the 409 the page reads as
            // locked): the server's administrator access is missing or stale, and preparing it again fixes it.
            throw new BadRequestException(
                "The mail server refused the administrator access: run \"Prepare administrator access\" on its mail page again.");
        }

        var expiresAt = credentials.Store(key, credential.Password, credential.Login);
        await audit.LogAsync("MailboxAdministratorOpened", "Mail", key.ServerId, account.Email, ct).ConfigureAwait(false);
        return MailboxService.ToAccess(account, expiresAt, administratorAccess: true) with { CanOpenAsAdministrator = true };
    }
}
