// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Configuration;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.Vaults;

public class VaultService(IVaultRepository repo, IEncryptionService encryption, IDbTransactionScope transaction, IAuditService audit, IEntityChangeNotifier notifier, TimeProvider timeProvider, IMemoryCache cache, IHttpContextAccessor? httpContextAccessor = null) : IVaultService
{
    private const string VaultNamesCachePrefix = "vaults:names:";
    private static readonly TimeSpan CacheDuration = BackendRuntimeDefaults.ReferenceDataCacheDuration;

    /// <summary>Shared by every instance of this scoped service: a name list cached while serving one
    /// request must be invalidated by another request that creates, renames or deletes a vault. Bumping
    /// it changes every cache key, so the stale lists are never read again and simply expire.</summary>
    private static long s_namesGeneration;

    /// <summary>The project names the vaults list's checkable filter offers.</summary>
    public Task<VaultFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default) =>
        repo.GetFilterValuesAsync(accessibleIds, ct);

    public async Task<PaginatedResult<VaultDto>> GetVaultsAsync(int? projectId, int? environmentId = null, int? projectServerId = null, PaginationRequest? request = null, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        request ??= new PaginationRequest();
        var (page, pageSize) = request.Normalize();
        var (items, totalCount) = await repo.GetVaultsPagedAsync(
            request.Search, projectId, environmentId, projectServerId, page, pageSize, accessibleIds, ct,
            request.SortBy, request.SortDescending, request.Filters).ConfigureAwait(false);

        return new PaginatedResult<VaultDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<VaultDetailDto?> GetVaultDetailAsync(int id, CancellationToken ct = default)
    {
        var vault = await repo.GetVaultDetailAsync(id, ct).ConfigureAwait(false);
        if (vault is null) return null;

        return new VaultDetailDto
        {
            Id = vault.Id,
            Name = vault.Name,
            Description = vault.Description,
            ProjectId = vault.ProjectId,
            ProjectName = vault.Project?.Name,
            EnvironmentId = vault.EnvironmentId,
            EnvironmentName = vault.Environment?.Name,
            ProjectServerId = vault.ProjectServerId,
            ProjectServerName = vault.ProjectServer?.DisplayName,
            SecretCount = vault.Secrets.Count,
            CreatedAt = vault.CreatedAt,
            UpdatedAt = vault.UpdatedAt,
            RowVersion = vault.RowVersion,
            Secrets = vault.Secrets.Select(s => new VaultSecretDto
            {
                Id = s.Id,
                Key = s.Key,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt,
                ExpiresAt = s.ExpiresAt,
                VersionCount = s.Versions.Count
            }).ToList()
        };
    }

    public async Task<List<string>> GetVaultNamesAsync(int? projectId, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var key = BuildNamesCacheKey(projectId, accessibleIds);
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await repo.GetVaultNamesAsync(projectId, accessibleIds, ct).ConfigureAwait(false);
        }).ConfigureAwait(false) ?? [];
    }

    public async Task<VaultDto> CreateVaultAsync(CreateVaultRequest request, CancellationToken ct = default)
    {
        var vault = new Vault
        {
            Name = request.Name,
            Description = request.Description,
            ProjectId = request.ProjectId,
            EnvironmentId = request.EnvironmentId,
            ProjectServerId = request.ProjectServerId
        };

        await repo.AddVaultAsync(vault, ct).ConfigureAwait(false);
        InvalidateNamesCache();
        await audit.LogAsync("Created", "Vault", vault.Id, vault.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Vault, vault.Id, EntityChangeOps.Created, ct).ConfigureAwait(false);
        return MapToDto(vault);
    }

    public async Task<VaultDto?> UpdateVaultAsync(int id, UpdateVaultRequest request, CancellationToken ct = default)
    {
        var vault = await repo.GetVaultDetailAsync(id, ct).ConfigureAwait(false);
        if (vault is null) return null;

        if (request.RowVersion != vault.RowVersion)
            throw new ConflictException("The vault was modified by another user. Please reload and try again.");

        vault.Name = request.Name;
        vault.Description = request.Description;
        vault.ProjectId = request.ProjectId;
        vault.EnvironmentId = request.EnvironmentId;
        vault.ProjectServerId = request.ProjectServerId;
        vault.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        vault.RowVersion = Guid.NewGuid();

        try
        {
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The vault was modified by another user. Please reload and try again.");
        }

        InvalidateNamesCache();
        await audit.LogAsync("Updated", "Vault", vault.Id, vault.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Vault, vault.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);
        return MapToDto(vault);
    }

    public async Task<bool> DeleteVaultAsync(int id, CancellationToken ct = default)
    {
        var vault = await repo.FindVaultAsync(id, ct).ConfigureAwait(false);
        if (vault is null) return false;

        await repo.RemoveVaultAsync(vault, ct).ConfigureAwait(false);
        InvalidateNamesCache();
        await audit.LogAsync("Deleted", "Vault", id, vault.Name, ct).ConfigureAwait(false);
        await notifier.BroadcastAsync(ResourceType.Vault, id, EntityChangeOps.Deleted, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<VaultSecretDto> CreateSecretAsync(int vaultId, CreateVaultSecretRequest request, CancellationToken ct = default)
    {
        var vault = await repo.FindVaultAsync(vaultId, ct).ConfigureAwait(false);
        if (vault is null) throw new NotFoundException("Vault not found");

        var secret = new VaultSecret
        {
            VaultId = vaultId,
            Key = request.Key,
            EncryptedValue = encryption.EncryptValue(request.Value),
            ExpiresAt = request.ExpiresAt
        };

        await transaction.ExecuteInTransactionAsync(async () =>
        {
            await repo.AddSecretAsync(secret, ct).ConfigureAwait(false);
            await AddSecretVersionAsync(secret, ChangeType.Created, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await audit.LogAsync("CreatedSecret", "VaultSecret", secret.Id, secret.Key, ct).ConfigureAwait(false);

        return ToDto(secret);
    }

    public async Task<VaultSecretDto?> UpdateSecretAsync(int vaultId, int secretId, UpdateVaultSecretRequest request, CancellationToken ct = default)
    {
        var secret = await repo.FindSecretAsync(secretId, ct).ConfigureAwait(false);
        if (secret is null || secret.VaultId != vaultId) return null;

        secret.Key = request.Key;
        secret.EncryptedValue = encryption.EncryptValue(request.Value);
        secret.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        secret.ExpiresAt = request.ExpiresAt;

        await PersistSecretChangeAsync(secret, ChangeType.Updated, "UpdatedSecret", ct).ConfigureAwait(false);

        return ToDto(secret);
    }

    public async Task<VaultSecretDto?> RotateSecretAsync(int vaultId, int secretId, RotateVaultSecretRequest request, CancellationToken ct = default)
    {
        var secret = await repo.FindSecretAsync(secretId, ct).ConfigureAwait(false);
        if (secret is null || secret.VaultId != vaultId) return null;

        // Hardening (#48): rotation must produce a different value, otherwise it would create a
        // spurious "Rotated" version with no actual change. Decrypt the current value and bail
        // out early if the caller submitted the same secret material.
        var currentValue = encryption.DecryptValue(secret.EncryptedValue);
        if (string.Equals(currentValue, request.Value, StringComparison.Ordinal))
            throw new BadRequestException("Rotation requires a new value.");

        secret.EncryptedValue = encryption.EncryptValue(request.Value);
        secret.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        secret.ExpiresAt = request.ExpiresAt;

        await PersistSecretChangeAsync(secret, ChangeType.Rotated, "Rotated", ct).ConfigureAwait(false);

        return ToDto(secret);
    }

    private async Task PersistSecretChangeAsync(
        VaultSecret secret,
        ChangeType changeType,
        string auditAction,
        CancellationToken ct)
    {
        await transaction.ExecuteInTransactionAsync(async () =>
        {
            await repo.SaveChangesAsync(ct).ConfigureAwait(false);
            await AddSecretVersionAsync(secret, changeType, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await audit.LogAsync(auditAction, "VaultSecret", secret.Id, secret.Key, ct).ConfigureAwait(false);
    }

    /// <summary> The clear value of one secret, so an operator can copy it. The value never goes
    /// into the audit entry; the entry records who read which key, so every copy leaves a trace.
    /// </summary>
    public async Task<string?> RevealSecretValueAsync(int vaultId, int secretId, CancellationToken ct = default)
    {
        var secret = await repo.FindSecretAsync(secretId, ct).ConfigureAwait(false);
        if (secret is null || secret.VaultId != vaultId) return null;

        var value = encryption.DecryptValue(secret.EncryptedValue);
        await audit.LogAsync("RevealedSecret", "VaultSecret", secret.Id, secret.Key, ct).ConfigureAwait(false);
        return value;
    }

    public async Task<bool> DeleteSecretAsync(int vaultId, int secretId, CancellationToken ct = default)
    {
        var secret = await repo.FindSecretAsync(secretId, ct).ConfigureAwait(false);
        if (secret is null || secret.VaultId != vaultId) return false;

        await transaction.ExecuteInTransactionAsync(async () =>
        {
            var version = await repo.GetNextVersionAsync(secret.Id, ct).ConfigureAwait(false);
            await repo.AddSecretVersionAsync(new VaultSecretVersion
            {
                VaultSecretId = secret.Id,
                Key = secret.Key,
                ChangedBy = CurrentUserName,
                ChangedAt = timeProvider.GetUtcNow().UtcDateTime,
                Version = version,
                ChangeType = ChangeType.Deleted
            }, ct).ConfigureAwait(false);

            await repo.RemoveSecretAsync(secret, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await audit.LogAsync("DeletedSecret", "VaultSecret", secret.Id, secret.Key, ct).ConfigureAwait(false);

        return true;
    }

    public async Task<List<VaultSecretVersionDto>> GetSecretVersionsAsync(int vaultId, int secretId, CancellationToken ct = default)
    {
        var secret = await repo.FindSecretAsync(secretId, ct).ConfigureAwait(false);
        if (secret is null || secret.VaultId != vaultId)
            throw new NotFoundException("Secret not found");

        var versions = await repo.GetSecretVersionsAsync(secretId, ct).ConfigureAwait(false);
        return versions.Select(v => new VaultSecretVersionDto
        {
            Version = v.Version,
            Key = v.Key,
            ChangedAt = v.ChangedAt,
            ChangeType = v.ChangeType,
            ChangedBy = v.ChangedBy
        }).ToList();
    }

    public async Task<int> PurgeHistoricalSecretVersionsAsync(
        int vaultId,
        int secretId,
        DateTime cutoffUtc,
        int maxCount,
        CancellationToken ct = default)
    {
        var secret = await repo.FindSecretAsync(secretId, ct).ConfigureAwait(false);
        if (secret is null || secret.VaultId != vaultId)
            return 0;
        var purged = await repo.PurgeHistoricalSecretVersionsAsync(secretId, cutoffUtc, maxCount, ct)
            .ConfigureAwait(false);
        if (purged > 0)
        {
            await audit.LogAsync(
                "PurgedSecretVersions",
                "VaultSecret",
                secretId,
                $"{purged} historical versions",
                ct).ConfigureAwait(false);
        }
        return purged;
    }

    public async Task<Dictionary<string, string>> ResolveVaultSecretsAsync(List<string> names, int? projectId, CancellationToken ct = default)
    {
        var vaults = await repo.FindByNamesAsync(names, projectId, ct).ConfigureAwait(false);

        return MergeVaults(vaults, vault => vault.ProjectId.HasValue ? 1 : 0);
    }

    public async Task<Dictionary<string, string>> ResolveVaultSecretsWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default)
    {
        var vaults = await repo.FindByNamesWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        return MergeVaultsWithPrecedence(vaults);
    }

    private Dictionary<string, string> MergeVaultsWithPrecedence(List<Data.Entities.Vault> vaults)
    {
        return MergeVaults(vaults, VaultPrecedence);
    }

    public async Task<(Dictionary<string, string> Vars, HashSet<string> FoundNames)> ResolveVaultSecretsWithNamesAsync(List<string> names, int? projectId, CancellationToken ct = default)
    {
        var vaults = await repo.FindByNamesAsync(names, projectId, ct).ConfigureAwait(false);
        var foundNames = new HashSet<string>(vaults.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);

        return (MergeVaults(vaults, vault => vault.ProjectId.HasValue ? 1 : 0), foundNames);
    }

    public async Task<(Dictionary<string, string> Vars, HashSet<string> FoundNames)> ResolveVaultSecretsWithCrossAccessAndNamesAsync(List<string> names, int projectId, CancellationToken ct = default)
    {
        var vaults = await repo.FindByNamesWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        var foundNames = new HashSet<string>(vaults.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        return (MergeVaultsWithPrecedence(vaults), foundNames);
    }

    /// <summary>The author of a history entry, as the audit log names it.</summary>
    private string CurrentUserName => httpContextAccessor?.HttpContext?.User?.Identity?.Name ?? "system";

    private async Task AddSecretVersionAsync(VaultSecret secret, ChangeType changeType, CancellationToken ct)
    {
        var version = await repo.GetNextVersionAsync(secret.Id, ct).ConfigureAwait(false);
        await repo.AddSecretVersionAsync(new VaultSecretVersion
        {
            VaultSecretId = secret.Id,
            Key = secret.Key,
            ChangedBy = CurrentUserName,
            ChangedAt = timeProvider.GetUtcNow().UtcDateTime,
            Version = version,
            ChangeType = changeType
        }, ct).ConfigureAwait(false);
    }

    private static VaultSecretDto ToDto(VaultSecret secret) => new()
    {
        Id = secret.Id,
        Key = secret.Key,
        CreatedAt = secret.CreatedAt,
        UpdatedAt = secret.UpdatedAt,
        ExpiresAt = secret.ExpiresAt
    };

    private Dictionary<string, string> MergeVaults(
        IEnumerable<Data.Entities.Vault> vaults, Func<Data.Entities.Vault, int> precedence)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vault in vaults.OrderBy(precedence))
        {
            foreach (var secret in vault.Secrets)
                result[secret.Key] = encryption.DecryptValue(secret.EncryptedValue);
        }
        return result;
    }

    public async Task<Dictionary<string, int>> ResolveSecretOwnersWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default)
    {
        var vaults = await repo.FindByNamesWithCrossAccessAsync(names, projectId, ct).ConfigureAwait(false);
        var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Same walk as the merge, so the last vault to set a name (the one whose value wins) owns it.
        foreach (var vault in vaults.OrderBy(VaultPrecedence))
            foreach (var secret in vault.Secrets)
                owners[secret.Key] = vault.Id;
        return owners;
    }

    private static int VaultPrecedence(Data.Entities.Vault vault)
    {
        if (vault.ProjectServerId.HasValue) return 3;
        if (vault.EnvironmentId.HasValue) return 2;
        return vault.ProjectId.HasValue ? 1 : 0;
    }

    public async Task<List<string>> ExportSecretKeysAsync(int vaultId, CancellationToken ct = default)
    {
        var vault = await repo.GetVaultDetailAsync(vaultId, ct).ConfigureAwait(false);
        if (vault is null) throw new NotFoundException("Vault not found");

        return vault.Secrets.Select(s => s.Key).ToList();
    }

    public async Task<int> ImportSecretsAsync(int vaultId, List<CreateVaultSecretRequest> secrets, CancellationToken ct = default)
    {
        var vault = await repo.FindVaultAsync(vaultId, ct).ConfigureAwait(false);
        if (vault is null) throw new NotFoundException("Vault not found");

        await transaction.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var createdSecrets = secrets.Select(req => new VaultSecret
            {
                VaultId = vaultId,
                Key = req.Key,
                EncryptedValue = encryption.EncryptValue(req.Value),
                ExpiresAt = req.ExpiresAt
            }).ToList();

            await repo.AddSecretsRangeAsync(createdSecrets, ct).ConfigureAwait(false);

            var versions = createdSecrets.Select(secret => new VaultSecretVersion
            {
                VaultSecretId = secret.Id,
                Key = secret.Key,
                ChangedBy = CurrentUserName,
                ChangedAt = timeProvider.GetUtcNow().UtcDateTime,
                Version = 1,
                ChangeType = ChangeType.Created
            }).ToList();

            await repo.AddSecretVersionsRangeAsync(versions, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        // A bulk import of secret material must leave an audit trail, mirroring CreatedSecret on the
        // single-create path.
        await audit.LogAsync("ImportedSecrets", "Vault", vaultId, $"{secrets.Count} secrets", ct).ConfigureAwait(false);

        return secrets.Count;
    }

    private static string BuildNamesCacheKey(int? projectId, List<int>? accessibleIds)
    {
        var pid = projectId?.ToString() ?? "all";
        var generation = Volatile.Read(ref s_namesGeneration);
        if (accessibleIds is null or [])
            return $"{VaultNamesCachePrefix}{generation}:{pid}";
        return $"{VaultNamesCachePrefix}{generation}:{pid}:{string.Join(',', accessibleIds.Order())}";
    }

    private static void InvalidateNamesCache() => Interlocked.Increment(ref s_namesGeneration);

    private static VaultDto MapToDto(Vault v) => new()
    {
        Id = v.Id,
        Name = v.Name,
        Description = v.Description,
        ProjectId = v.ProjectId,
        ProjectName = v.Project?.Name,
        EnvironmentId = v.EnvironmentId,
        EnvironmentName = v.Environment?.Name,
        ProjectServerId = v.ProjectServerId,
        ProjectServerName = v.ProjectServer?.DisplayName,
        SecretCount = v.Secrets.Count,
        CreatedAt = v.CreatedAt,
        UpdatedAt = v.UpdatedAt,
        RowVersion = v.RowVersion
    };
}
