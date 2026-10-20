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

    public Task<string?> GetEncryptedSecretAsync(string key, CancellationToken ct = default) =>
        db.Secrets.AsNoTracking()
            .Where(secret => secret.Key == key)
            .Select(secret => secret.EncryptedValue)
            .FirstOrDefaultAsync(ct);

    public async Task<(List<MailboxDirectoryEntryDto> Items, int Total)> ListDirectoryAsync(
        IReadOnlyCollection<int>? serverIds, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default)
    {
        var query = db.MailAccounts.AsNoTracking()
            .Where(account => account.IsActive && account.MissingSince == null);
        if (serverIds is not null)
            query = query.Where(account => serverIds.Contains(account.MailDomain.ServerId));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(account => EF.Functions.ILike(account.Email, pattern)
                || EF.Functions.ILike(account.MailDomain.Server.Name, pattern));
        }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("servername", false) => query.OrderBy(account => account.MailDomain.Server.Name).ThenBy(account => account.Email).ThenBy(account => account.Id),
            ("servername", true) => query.OrderByDescending(account => account.MailDomain.Server.Name).ThenBy(account => account.Email).ThenBy(account => account.Id),
            (_, true) => query.OrderByDescending(account => account.Email).ThenBy(account => account.Id),
            _ => query.OrderBy(account => account.Email).ThenBy(account => account.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(account => new MailboxDirectoryEntryDto
            {
                ServerId = account.MailDomain.ServerId,
                ServerName = account.MailDomain.Server.Name,
                AccountId = account.Id,
                Email = account.Email,
                Domain = account.MailDomain.Name,
                QuotaMb = account.QuotaMb,
                UsedMb = account.UsedMb
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }
}
