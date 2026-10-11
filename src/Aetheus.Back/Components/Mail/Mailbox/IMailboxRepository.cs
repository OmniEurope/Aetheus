// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

public interface IMailboxRepository
{
    /// <summary>The active mailbox of that server, or null.</summary>
    Task<MailboxAccount?> FindAccountAsync(int serverId, int accountId, CancellationToken ct = default);

    /// <summary>The host name mail clients connect to for that server: the mail host name first, or empty.</summary>
    Task<string> GetMailHostAsync(int serverId, CancellationToken ct = default);
}
