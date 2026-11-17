// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AgentPools;

public interface IAgentPoolRepository
{
    Task<(List<AgentPool> Items, int TotalCount)> GetPoolsPagedAsync(
        string? search, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<AgentPool?> GetPoolWithServersAsync(int id, CancellationToken ct = default);
    Task<AgentPool?> FindPoolAsync(int id, CancellationToken ct = default);
    Task<AgentPool?> FindByNameAsync(string name, CancellationToken ct = default);
    /// <summary>Whether another pool already has <paramref name="name"/> (the name is unique on the whole platform).</summary>
    Task<bool> NameTakenAsync(string name, int? exceptPoolId, CancellationToken ct = default);
    /// <summary>Whether every server of <paramref name="serverIds"/> exists and belongs to <paramref name="organizationId"/>.</summary>
    Task<bool> AllServersInOrganizationAsync(int organizationId, IReadOnlyCollection<int> serverIds, CancellationToken ct = default);
    /// <summary>The default organization (slug <c>aetheus</c>), where a site Admin's pool without a named organization goes.</summary>
    Task<int?> GetDefaultOrganizationIdAsync(CancellationToken ct = default);
    Task AddPoolAsync(AgentPool pool, CancellationToken ct = default);
    Task RemovePoolAsync(AgentPool pool, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
