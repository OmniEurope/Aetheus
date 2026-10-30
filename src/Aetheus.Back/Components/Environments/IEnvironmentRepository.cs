// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Components.Environments;

public interface IEnvironmentRepository
{
    Task<(List<Environment> Items, int TotalCount)> GetEnvironmentsPagedAsync(
        string? search, int? projectId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? columnFilters = null);
    Task<Environment?> GetEnvironmentWithServersAsync(int id, CancellationToken ct = default);
    Task<Environment?> FindEnvironmentAsync(int id, CancellationToken ct = default);
    Task<Environment?> FindByNameAsync(string name, CancellationToken ct = default);
    Task AddEnvironmentAsync(Environment environment, CancellationToken ct = default);
    Task RemoveEnvironmentAsync(Environment environment, CancellationToken ct = default);
    Task<Environment?> GetEnvironmentForDuplicationAsync(int id, CancellationToken ct = default);
    Task<EnvironmentCopyReferences?> GetCopyReferencesAsync(int id, CancellationToken ct = default);
    Task<bool> NameExistsInProjectAsync(string name, int? projectId, CancellationToken ct = default);
    Task<int?> GetProjectServerProjectIdAsync(int projectServerId, CancellationToken ct = default);
    Task<bool> LinkExistsAsync(int envId, int projectServerId, CancellationToken ct = default);
    Task AddLinkAsync(EnvironmentProjectServer link, CancellationToken ct = default);
    Task RemoveLinkAsync(int envId, int projectServerId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
