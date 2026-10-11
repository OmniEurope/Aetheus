// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

public sealed class EmailSenderSettingsRepository(AppDbContext db) : IEmailSenderSettingsRepository
{
    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct = default)
    {
        var rows = await db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith(EmailSenderSettingKeys.Prefix))
            .Select(setting => new { setting.Key, setting.Value })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    }

    public Task<string?> GetEncryptedSecretAsync(string key, CancellationToken ct = default) =>
        db.Secrets.AsNoTracking()
            .Where(secret => secret.Key == key)
            .Select(secret => secret.EncryptedValue)
            .FirstOrDefaultAsync(ct);

    public async Task<ManagedMailDomain?> FindDefaultMailDomainAsync(CancellationToken ct = default)
    {
        // A domain its server still reports, on a server whose mail stack reports a state. The stack
        // Aetheus set up itself comes first, then the oldest domain, so the choice stays stable.
        var candidate = await db.MailDomains.AsNoTracking()
            .Where(domain => domain.IsActive && domain.MissingSince == null
                && db.MailStates.Any(state => state.ServerId == domain.ServerId))
            .Select(domain => new
            {
                domain.Id,
                domain.ServerId,
                domain.Name,
                IsManaged = db.MailStates.Any(state => state.ServerId == domain.ServerId && state.IsManagedByAetheus),
                MailHostname = db.MailStates
                    .Where(state => state.ServerId == domain.ServerId)
                    .Select(state => state.Hostname)
                    .FirstOrDefault(),
                domain.Server.Hostname,
                domain.Server.IpAddress
            })
            .OrderByDescending(row => row.IsManaged)
            .ThenBy(row => row.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (candidate is null) return null;

        var host = new[] { candidate.MailHostname, candidate.Hostname, candidate.IpAddress }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        return host is null ? null : new ManagedMailDomain(candidate.ServerId, candidate.Name, host);
    }

    public Task<int?> FindActiveMailboxIdAsync(string address, CancellationToken ct = default)
    {
        var normalized = address.ToLowerInvariant();
        return db.MailAccounts.AsNoTracking()
            .Where(account => account.IsActive && account.MissingSince == null
                && account.Email.ToLower() == normalized)
            .Select(account => (int?)account.Id)
            .FirstOrDefaultAsync(ct);
    }
}
