// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

/// <summary>
/// Reads what the sender resolution needs over the shared entities (platform settings, secret store,
/// mail domains and mailboxes), so this shared service injects no Components module.
/// </summary>
public interface IEmailSenderSettingsRepository
{
    /// <summary>The platform settings whose key starts with <see cref="EmailSenderSettingKeys.Prefix"/>.</summary>
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>The encrypted value of a platform secret, or null when it is not stored.</summary>
    Task<string?> GetEncryptedSecretAsync(string key, CancellationToken ct = default);

    /// <summary>The mail domain the default sender uses, or null when Aetheus knows none.</summary>
    Task<ManagedMailDomain?> FindDefaultMailDomainAsync(CancellationToken ct = default);

    /// <summary>The id of the active mailbox of that address (case-insensitive), or null.</summary>
    Task<int?> FindActiveMailboxIdAsync(string address, CancellationToken ct = default);
}
