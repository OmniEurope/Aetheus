// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>The stored exclusion rules of the system logs, one set per organization (R-28).</summary>
public interface ISystemLogExclusionRepository
{
    Task<bool> OrganizationExistsAsync(int organizationId, CancellationToken ct = default);

    Task<List<SystemLogExclusionRule>> ListAsync(int organizationId, bool enabledOnly, CancellationToken ct = default);

    /// <summary>The rule of the organization, tracked for update; null when the organization has no such rule.</summary>
    Task<SystemLogExclusionRule?> FindAsync(int organizationId, int ruleId, CancellationToken ct = default);

    Task<bool> ExistsAsync(int organizationId, string pattern, string category, int? exceptRuleId, CancellationToken ct = default);

    Task AddAsync(SystemLogExclusionRule rule, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);

    Task DeleteAsync(SystemLogExclusionRule rule, CancellationToken ct = default);
}
