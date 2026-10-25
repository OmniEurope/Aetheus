// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Vaults;

public interface IVaultService
{
    Task<VaultFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default);
    Task<PaginatedResult<VaultDto>> GetVaultsAsync(int? projectId, int? environmentId = null, int? projectServerId = null, PaginationRequest? request = null, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<VaultDetailDto?> GetVaultDetailAsync(int id, CancellationToken ct = default);
    Task<List<string>> GetVaultNamesAsync(int? projectId, List<int>? accessibleIds = null, CancellationToken ct = default);
    Task<VaultDto> CreateVaultAsync(CreateVaultRequest request, CancellationToken ct = default);
    Task<VaultDto?> UpdateVaultAsync(int id, UpdateVaultRequest request, CancellationToken ct = default);
    Task<bool> DeleteVaultAsync(int id, CancellationToken ct = default);
    Task<VaultSecretDto> CreateSecretAsync(int vaultId, CreateVaultSecretRequest request, CancellationToken ct = default);
    Task<VaultSecretDto?> UpdateSecretAsync(int vaultId, int secretId, UpdateVaultSecretRequest request, CancellationToken ct = default);
    Task<VaultSecretDto?> RotateSecretAsync(int vaultId, int secretId, RotateVaultSecretRequest request, CancellationToken ct = default);
    Task<bool> DeleteSecretAsync(int vaultId, int secretId, CancellationToken ct = default);
    /// <summary>One secret's clear value, for the copy button; every read is audited.</summary>
    Task<string?> RevealSecretValueAsync(int vaultId, int secretId, CancellationToken ct = default);
    Task<List<VaultSecretVersionDto>> GetSecretVersionsAsync(int vaultId, int secretId, CancellationToken ct = default);
    Task<int> PurgeHistoricalSecretVersionsAsync(
        int vaultId,
        int secretId,
        DateTime cutoffUtc,
        int maxCount,
        CancellationToken ct = default);
    Task<Dictionary<string, string>> ResolveVaultSecretsAsync(List<string> names, int? projectId, CancellationToken ct = default);
    Task<Dictionary<string, string>> ResolveVaultSecretsWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default);
    Task<(Dictionary<string, string> Vars, HashSet<string> FoundNames)> ResolveVaultSecretsWithNamesAsync(List<string> names, int? projectId, CancellationToken ct = default);
    Task<(Dictionary<string, string> Vars, HashSet<string> FoundNames)> ResolveVaultSecretsWithCrossAccessAndNamesAsync(List<string> names, int projectId, CancellationToken ct = default);
    /// <summary>For each secret name of the named vaults, the id of the vault whose value a run of the project
    /// would read (same precedence and cross-project access as <see cref="ResolveVaultSecretsWithCrossAccessAndNamesAsync"/>).</summary>
    Task<Dictionary<string, int>> ResolveSecretOwnersWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default);
    Task<List<string>> ExportSecretKeysAsync(int vaultId, CancellationToken ct = default);
    Task<int> ImportSecretsAsync(int vaultId, List<CreateVaultSecretRequest> secrets, CancellationToken ct = default);
}
