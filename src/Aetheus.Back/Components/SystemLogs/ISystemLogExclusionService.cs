// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>An organization's exclusion rules of the system logs (R-28).</summary>
public interface ISystemLogExclusionService
{
    Task<List<SystemLogExclusionRuleDto>> ListAsync(int organizationId, CancellationToken ct = default);

    /// <summary>The rules that are on, for the logs query to split the entries by.</summary>
    Task<List<SystemLogExclusionRuleDto>> ListEnabledAsync(int organizationId, CancellationToken ct = default);

    Task<SystemLogExclusionRuleDto> CreateAsync(
        int organizationId, SaveSystemLogExclusionRuleRequest request, string? username, CancellationToken ct = default);

    Task<SystemLogExclusionRuleDto> SetEnabledAsync(int organizationId, int ruleId, bool enabled, CancellationToken ct = default);

    Task DeleteAsync(int organizationId, int ruleId, CancellationToken ct = default);
}
