// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Vaults;

public interface IVaultRepository
{
    Task<(List<Vault> Items, int TotalCount)> GetVaultsPagedAsync(
        string? search, int? projectId, int? environmentId, int? projectServerId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? columnFilters = null);

    Task<VaultFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default);

    Task<Vault?> GetVaultDetailAsync(int id, CancellationToken ct = default);

    Task<List<Vault>> FindByNamesAsync(List<string> names, int? projectId, CancellationToken ct = default);

    Task<List<Vault>> FindByNamesWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default);

    /// <summary>R-25: the vaults a run of the project can read that hold one of these keys, as (vault id, vault name, key).
    /// Key names only: no value is read. The project's own vaults come first.</summary>
    Task<List<(int Id, string Name, string Key)>> FindKeyHoldersWithCrossAccessAsync(
        IReadOnlyCollection<string> keys, int projectId, CancellationToken ct = default);

    Task<List<string>> GetVaultNamesAsync(int? projectId, List<int>? accessibleIds = null, CancellationToken ct = default);

    Task<Vault?> FindVaultAsync(int id, CancellationToken ct = default);

    Task AddVaultAsync(Vault vault, CancellationToken ct = default);

    Task RemoveVaultAsync(Vault vault, CancellationToken ct = default);

    Task<VaultSecret?> FindSecretAsync(int secretId, CancellationToken ct = default);

    Task AddSecretAsync(VaultSecret secret, CancellationToken ct = default);

    Task AddSecretsRangeAsync(List<VaultSecret> secrets, CancellationToken ct = default);

    Task RemoveSecretAsync(VaultSecret secret, CancellationToken ct = default);

    Task AddSecretVersionAsync(VaultSecretVersion version, CancellationToken ct = default);

    Task AddSecretVersionsRangeAsync(List<VaultSecretVersion> versions, CancellationToken ct = default);

    Task<List<VaultSecretVersion>> GetSecretVersionsAsync(int secretId, CancellationToken ct = default);
    Task<int> CountSecretVersionsAsync(int secretId, CancellationToken ct = default);
    Task<int> PurgeHistoricalSecretVersionsAsync(
        int secretId,
        DateTime cutoffUtc,
        int maxCount,
        CancellationToken ct = default);

    Task<int> GetNextVersionAsync(int secretId, CancellationToken ct = default);

    Task<List<VaultSecret>> GetExpiringSecretsAsync(DateTime threshold, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
