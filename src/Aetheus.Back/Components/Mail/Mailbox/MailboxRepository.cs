// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

public sealed class MailboxRepository(AppDbContext db) : IMailboxRepository
{
    public async Task<MailboxAccount?> FindAccountAsync(int serverId, int accountId, CancellationToken ct = default)
    {
        var email = await db.MailAccounts.AsNoTracking()
            .Where(account => account.Id == accountId && account.MailDomain.ServerId == serverId
                && account.IsActive && account.MissingSince == null)
            .Select(account => account.Email)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (email is null) return null;
        var host = await GetMailHostAsync(serverId, ct).ConfigureAwait(false);
        return new MailboxAccount(accountId, serverId, email, host);
    }

    public async Task<string> GetMailHostAsync(int serverId, CancellationToken ct = default)
    {
        var mailHostname = await db.MailStates.AsNoTracking()
            .Where(state => state.ServerId == serverId)
            .Select(state => state.Hostname)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var server = await db.Servers.AsNoTracking()
            .Where(candidate => candidate.Id == serverId)
            .Select(candidate => new { candidate.Hostname, candidate.IpAddress })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // The mail host name first: it is the name the mail server's TLS certificate is issued for.
        return new[] { mailHostname, server?.Hostname, server?.IpAddress }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
