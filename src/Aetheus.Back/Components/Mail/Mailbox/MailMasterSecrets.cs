// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Settings;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Recette R1-41: the master password of each server, in the platform secret store (AES-256).</summary>
public interface IMailMasterSecrets
{
    /// <summary>Stores (or replaces) the master password of the server.</summary>
    Task StoreAsync(int serverId, string password, CancellationToken ct = default);

    /// <summary>The master password of the server, or null when none was prepared.</summary>
    Task<string?> ReadAsync(int serverId, CancellationToken ct = default);
}

/// <summary>
/// Writes through the settings service (the secret upsert is audited by key, never by value) and reads
/// the encrypted row back through the mailbox repository. Neither the password nor its cipher text is logged.
/// </summary>
public sealed class MailMasterSecrets(
    ISettingsService settings,
    IMailboxRepository repository,
    IEncryptionService encryption) : IMailMasterSecrets
{
    public async Task StoreAsync(int serverId, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        await settings.CreateSecretAsync(new CreateSecretRequest
        {
            Key = MailMasterAccess.SecretKey(serverId),
            Value = password
        }, ct).ConfigureAwait(false);
    }

    public async Task<string?> ReadAsync(int serverId, CancellationToken ct = default)
    {
        var encrypted = await repository.GetEncryptedSecretAsync(MailMasterAccess.SecretKey(serverId), ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(encrypted) ? null : encryption.DecryptValue(encrypted);
    }
}
