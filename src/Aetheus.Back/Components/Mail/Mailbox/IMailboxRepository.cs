// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

public interface IMailboxRepository
{
    /// <summary>The active mailbox of that server, or null.</summary>
    Task<MailboxAccount?> FindAccountAsync(int serverId, int accountId, CancellationToken ct = default);

    /// <summary>The host name mail clients connect to for that server: the mail host name first, or empty.</summary>
    Task<string> GetMailHostAsync(int serverId, CancellationToken ct = default);

    /// <summary>Recette R1-41: the encrypted value of a platform secret, or null.</summary>
    Task<string?> GetEncryptedSecretAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Recette R1-41: one page of the active mailboxes of <paramref name="serverIds"/> (every server when null),
    /// with their server's name, for the "Messagerie" page.
    /// </summary>
    Task<(List<MailboxDirectoryEntryDto> Items, int Total)> ListDirectoryAsync(
        IReadOnlyCollection<int>? serverIds, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default);
}
