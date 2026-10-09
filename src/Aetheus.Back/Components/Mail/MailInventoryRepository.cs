// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Mail;

public class MailInventoryRepository(AppDbContext db) : IMailInventoryRepository
{
    private static readonly OperationKind[] s_removalKinds =
        [OperationKind.MailRemoveDomain, OperationKind.MailDeleteAccount, OperationKind.MailRemoveAlias, OperationKind.MailPurge];

    private static readonly TaskExecutionStatus[] s_openStatuses =
        [TaskExecutionStatus.Pending, TaskExecutionStatus.Assigned, TaskExecutionStatus.Running];

    public async Task<List<MailDomain>> GetTrackedInventoryAsync(int serverId, CancellationToken ct = default) =>
        await db.MailDomains
            .Where(d => d.ServerId == serverId)
            .Include(d => d.Accounts)
            .Include(d => d.Aliases)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<HashSet<string>> GetAccountEmailsOnOtherServersAsync(
        int serverId, IReadOnlyCollection<string> emails, CancellationToken ct = default)
    {
        if (emails.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = await db.MailAccounts
            .AsNoTracking()
            .Where(a => a.MailDomain.ServerId != serverId && emails.Contains(a.Email))
            .Select(a => a.Email)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<HashSet<(OperationKind Kind, string Target)>> GetRecentRemovalTargetsAsync(
        int serverId, DateTime since, CancellationToken ct = default)
    {
        var rows = await db.Tasks
            .AsNoTracking()
            .Where(t => t.ServerId == serverId
                && s_removalKinds.Contains(t.Operation)
                && (s_openStatuses.Contains(t.Status) || t.CreatedAt >= since))
            .Select(t => new { t.Operation, t.Command })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => (r.Operation, r.Command.ToLowerInvariant())).ToHashSet();
    }

    public async Task<MailState?> GetStateAsync(int serverId, CancellationToken ct = default) =>
        await db.MailStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ServerId == serverId, ct)
            .ConfigureAwait(false);

    public async Task<MailDomain?> GetTrackedDomainAsync(int serverId, int domainId, CancellationToken ct = default) =>
        await db.MailDomains
            .FirstOrDefaultAsync(d => d.ServerId == serverId && d.Id == domainId, ct)
            .ConfigureAwait(false);

    public async Task<List<MailDomain>> GetTrackedDomainsDueForDnsCheckAsync(
        DateTime checkedBefore, int take, CancellationToken ct = default) =>
        await db.MailDomains
            .Where(d => d.IsActive && (d.DnsCheckedAt == null || d.DnsCheckedAt < checkedBefore))
            .OrderBy(d => d.DnsCheckedAt)
            .ThenBy(d => d.Id)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<OperationKind?> GetTaskOperationAsync(int serverId, int taskId, CancellationToken ct = default) =>
        await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId && t.ServerId == serverId)
            .Select(t => (OperationKind?)t.Operation)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public void AddDomain(MailDomain domain) => db.MailDomains.Add(domain);

    public void RemoveDomain(MailDomain domain) => db.MailDomains.Remove(domain);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
