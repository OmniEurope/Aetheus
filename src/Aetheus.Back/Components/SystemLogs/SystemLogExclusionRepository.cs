// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Npgsql;

namespace Aetheus.Back.Components.SystemLogs;

public sealed class SystemLogExclusionRepository(AppDbContext db) : ISystemLogExclusionRepository
{
    public Task<bool> OrganizationExistsAsync(int organizationId, CancellationToken ct = default) =>
        db.Organizations.AsNoTracking().AnyAsync(organization => organization.Id == organizationId, ct);

    public Task<List<SystemLogExclusionRule>> ListAsync(int organizationId, bool enabledOnly, CancellationToken ct = default) =>
        db.SystemLogExclusionRules.AsNoTracking()
            .Where(rule => rule.OrganizationId == organizationId && (!enabledOnly || rule.Enabled))
            .OrderBy(rule => rule.Pattern).ThenBy(rule => rule.Category)
            .ToListAsync(ct);

    public Task<SystemLogExclusionRule?> FindAsync(int organizationId, int ruleId, CancellationToken ct = default) =>
        db.SystemLogExclusionRules.FirstOrDefaultAsync(rule => rule.OrganizationId == organizationId && rule.Id == ruleId, ct);

    public Task<bool> ExistsAsync(int organizationId, string pattern, string category, int? exceptRuleId, CancellationToken ct = default) =>
        db.SystemLogExclusionRules.AsNoTracking().AnyAsync(
            rule => rule.OrganizationId == organizationId && rule.Pattern == pattern && rule.Category == category
                    && (exceptRuleId == null || rule.Id != exceptRuleId), ct);

    public async Task AddAsync(SystemLogExclusionRule rule, CancellationToken ct = default)
    {
        db.SystemLogExclusionRules.Add(rule);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        // Two administrators adding the same rule at once: the unique index answers, not the check before it.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("This exclusion rule already exists.");
        }
    }

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public async Task DeleteAsync(SystemLogExclusionRule rule, CancellationToken ct = default)
    {
        db.SystemLogExclusionRules.Remove(rule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
