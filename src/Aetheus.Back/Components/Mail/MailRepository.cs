// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

public class MailRepository(AppDbContext db) : IMailRepository
{
    public async Task<MailState?> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        return await db.MailStates
            .AsNoTracking()
            .Where(m => m.ServerId == serverId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<MailDomain>> GetDomainsAsync(int serverId, CancellationToken ct = default)
    {
        return await db.MailDomains
            .AsNoTracking()
            .Where(d => d.ServerId == serverId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<MailDomain> Items, int Total)> GetDomainsPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = db.MailDomains.AsNoTracking().Where(item => item.ServerId == serverId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(item => EF.Functions.ILike(item.Name, pattern));
        }
        // Recette R-210: the header filters, after the server scope and before the count.
        query = MailListQuery.DomainColumns.ApplyFilters(query, filters);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("isactive", false) => query.OrderBy(item => item.IsActive).ThenBy(item => item.Name).ThenBy(item => item.Id),
            ("isactive", true) => query.OrderByDescending(item => item.IsActive).ThenBy(item => item.Name).ThenBy(item => item.Id),
            ("createdat", false) => query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id),
            ("createdat", true) => query.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id),
            ("dkimselector", false) => query.OrderBy(item => item.DkimSelector).ThenBy(item => item.Name).ThenBy(item => item.Id),
            ("dkimselector", true) => query.OrderByDescending(item => item.DkimSelector).ThenBy(item => item.Name).ThenBy(item => item.Id),
            (_, true) => query.OrderByDescending(item => item.Name).ThenBy(item => item.Id),
            _ => query.OrderBy(item => item.Name).ThenBy(item => item.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task<MailDomain?> GetDomainAsync(int serverId, int domainId, CancellationToken ct = default)
    {
        return await db.MailDomains
            .Where(d => d.ServerId == serverId && d.Id == domainId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<MailAccount>> GetAccountsAsync(int serverId, CancellationToken ct = default)
    {
        return await db.MailAccounts
            .AsNoTracking()
            .Where(a => a.MailDomain.ServerId == serverId)
            .OrderBy(a => a.Email)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<MailAccount> Items, int Total)> GetAccountsPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = db.MailAccounts.AsNoTracking().Where(item => item.MailDomain.ServerId == serverId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(item => EF.Functions.ILike(item.Email, pattern));
        }
        // Recette R-210 / R-224: the header filters, after the server scope and before the count.
        query = MailListQuery.AccountColumns.ApplyFilters(query, filters);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("quotamb", false) => query.OrderBy(item => item.QuotaMb).ThenBy(item => item.Email).ThenBy(item => item.Id),
            ("quotamb", true) => query.OrderByDescending(item => item.QuotaMb).ThenBy(item => item.Email).ThenBy(item => item.Id),
            ("isactive", false) => query.OrderBy(item => item.IsActive).ThenBy(item => item.Email).ThenBy(item => item.Id),
            ("isactive", true) => query.OrderByDescending(item => item.IsActive).ThenBy(item => item.Email).ThenBy(item => item.Id),
            ("createdat", false) => query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id),
            ("createdat", true) => query.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id),
            (_, true) => query.OrderByDescending(item => item.Email).ThenBy(item => item.Id),
            _ => query.OrderBy(item => item.Email).ThenBy(item => item.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task<MailAccount?> GetAccountAsync(int accountId, CancellationToken ct = default)
    {
        return await db.MailAccounts
            .Where(a => a.Id == accountId)
            .Include(a => a.MailDomain)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<MailDomain?> FindDomainByNameAsync(int serverId, string name, CancellationToken ct = default)
    {
        var lowered = name.ToLowerInvariant();
        return await db.MailDomains
            .Where(d => d.ServerId == serverId && d.Name.ToLower() == lowered)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<MailAccount?> FindAccountByEmailAsync(string email, CancellationToken ct = default)
    {
        var lowered = email.ToLowerInvariant();
        return await db.MailAccounts
            .Where(a => a.Email.ToLower() == lowered)
            .Include(a => a.MailDomain)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddDomainAsync(MailDomain domain, CancellationToken ct = default)
    {
        db.MailDomains.Add(domain);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateDomainAsync(MailDomain domain, CancellationToken ct = default)
    {
        db.MailDomains.Update(domain);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteDomainAsync(MailDomain domain, CancellationToken ct = default)
    {
        db.MailDomains.Remove(domain);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAccountAsync(MailAccount account, CancellationToken ct = default)
    {
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAccountAsync(MailAccount account, CancellationToken ct = default)
    {
        db.MailAccounts.Update(account);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAccountAsync(MailAccount account, CancellationToken ct = default)
    {
        db.MailAccounts.Remove(account);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddTaskAsync(ServerTask task, CancellationToken ct = default)
    {
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> HasOpenTaskAsync(int serverId, IReadOnlyCollection<OperationKind> kinds, CancellationToken ct = default) =>
        await db.Tasks
            .AsNoTracking()
            .AnyAsync(t => t.ServerId == serverId
                && kinds.Contains(t.Operation)
                && (t.Status == TaskExecutionStatus.Pending
                    || t.Status == TaskExecutionStatus.Assigned
                    || t.Status == TaskExecutionStatus.Running), ct)
            .ConfigureAwait(false);

    // --- Aliases ---

    public async Task<List<MailAlias>> GetAliasesAsync(int serverId, CancellationToken ct = default)
    {
        return await db.MailAliases
            .AsNoTracking()
            .Where(a => a.MailDomain.ServerId == serverId)
            .OrderBy(a => a.SourceEmail)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<(List<MailAlias> Items, int Total)> GetAliasesPagedAsync(
        int serverId, string? search, int page, int pageSize, string? sortBy, bool sortDescending,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = db.MailAliases.AsNoTracking().Where(item => item.MailDomain.ServerId == serverId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(item =>
                EF.Functions.ILike(item.SourceEmail, pattern) ||
                EF.Functions.ILike(item.DestinationEmail, pattern));
        }
        // Recette R-210: the header filters, after the server scope and before the count.
        query = MailListQuery.AliasColumns.ApplyFilters(query, filters);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        query = (sortBy?.Trim().ToLowerInvariant(), sortDescending) switch
        {
            ("destinationemail", false) => query.OrderBy(item => item.DestinationEmail).ThenBy(item => item.SourceEmail).ThenBy(item => item.Id),
            ("destinationemail", true) => query.OrderByDescending(item => item.DestinationEmail).ThenBy(item => item.SourceEmail).ThenBy(item => item.Id),
            ("isactive", false) => query.OrderBy(item => item.IsActive).ThenBy(item => item.SourceEmail).ThenBy(item => item.Id),
            ("isactive", true) => query.OrderByDescending(item => item.IsActive).ThenBy(item => item.SourceEmail).ThenBy(item => item.Id),
            ("createdat", false) => query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id),
            ("createdat", true) => query.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id),
            (_, true) => query.OrderByDescending(item => item.SourceEmail).ThenBy(item => item.Id),
            _ => query.OrderBy(item => item.SourceEmail).ThenBy(item => item.Id)
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    public async Task<MailAlias?> GetAliasAsync(int aliasId, CancellationToken ct = default)
    {
        return await db.MailAliases
            .Where(a => a.Id == aliasId)
            .Include(a => a.MailDomain)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddAliasAsync(MailAlias alias, CancellationToken ct = default)
    {
        db.MailAliases.Add(alias);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAliasAsync(MailAlias alias, CancellationToken ct = default)
    {
        db.MailAliases.Update(alias);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAliasAsync(MailAlias alias, CancellationToken ct = default)
    {
        db.MailAliases.Remove(alias);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
