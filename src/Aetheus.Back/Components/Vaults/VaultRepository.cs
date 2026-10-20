// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Vaults;

public class VaultRepository(AppDbContext db, TimeProvider timeProvider) : IVaultRepository
{
    /// <summary>The header filters of the vaults list.</summary>
    internal static readonly GridQueryMap<Vault> Columns = new GridQueryMap<Vault>()
        .Text("Name", v => v.Name)
        .Text("ProjectName", v => v.Project != null ? v.Project.Name : null)
        .Text("Description", v => v.Description)
        .Number("SecretCount", v => v.Secrets.Count)
        .Date("CreatedAt", v => v.CreatedAt)
        .Date("UpdatedAt", v => v.UpdatedAt);

    /// <summary>The project names present across the vaults the caller can read.</summary>
    public async Task<VaultFilterValuesDto> GetFilterValuesAsync(List<int>? accessibleIds, CancellationToken ct = default)
    {
        var query = db.Vaults.AsNoTracking().Where(v => v.Project != null);
        if (accessibleIds is not null)
            query = query.Where(v => accessibleIds.Contains(v.Id));
        var names = await query.Select(v => v.Project!.Name).Distinct().ToListAsync(ct).ConfigureAwait(false);
        return new VaultFilterValuesDto { ProjectNames = [.. names.Order(StringComparer.OrdinalIgnoreCase)] };
    }

    public async Task<(List<Vault> Items, int TotalCount)> GetVaultsPagedAsync(
        string? search, int? projectId, int? environmentId, int? projectServerId, int page, int pageSize, List<int>? accessibleIds = null, CancellationToken ct = default,
        string? sortBy = null, bool sortDescending = false, IReadOnlyList<GridFilter>? columnFilters = null)
    {
        var query = db.Vaults.AsNoTracking().AsQueryable();

        if (accessibleIds is not null)
            query = query.Where(v => accessibleIds.Contains(v.Id));

        if (environmentId.HasValue)
            query = query.Where(v => v.EnvironmentId == environmentId.Value);
        else if (projectServerId.HasValue)
            query = query.Where(v => v.ProjectServerId == projectServerId.Value);
        else if (projectId.HasValue)
            query = query.Where(v => v.ProjectId == projectId.Value || v.ProjectId == null);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(v => v.Name.Contains(search) || v.Description.Contains(search));

        query = Columns.ApplyFilters(query, columnFilters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);

        var rows = query
            .Include(v => v.Project)
            .Include(v => v.Environment)
            .Include(v => v.ProjectServer)
            .Include(v => v.Secrets);

        // The list shows the owning project as its own column, but ProjectName is not a property of
        // the entity: resolved by name it would miss and fall back to Name, so the sort header would
        // move the arrow and leave the rows untouched. Mapped to the navigation, as BackupRepository
        // and GitLightRepository already do for the same column.
        IQueryable<Vault> ordered = string.Equals(sortBy, "ProjectName", StringComparison.OrdinalIgnoreCase)
            ? sortDescending
                ? rows.OrderByDescending(v => v.Project!.Name)
                : rows.OrderBy(v => v.Project!.Name)
            : rows.OrderByProperty(sortBy, sortDescending, v => v.Name, fallbackDescending: false);

        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<Vault?> GetVaultDetailAsync(int id, CancellationToken ct = default)
    {
        return await db.Vaults
            .Where(v => v.Id == id)
            .Include(v => v.Project)
            .Include(v => v.Environment)
            .Include(v => v.ProjectServer)
            .Include(v => v.Secrets)
                .ThenInclude(s => s.Versions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Vault>> FindByNamesAsync(List<string> names, int? projectId, CancellationToken ct = default)
    {
        return await db.Vaults
            .AsNoTracking()
            .Where(v => names.Contains(v.Name)
                && (v.ProjectId == projectId || v.ProjectId == null))
            .Include(v => v.Secrets)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Vault>> FindByNamesWithCrossAccessAsync(List<string> names, int projectId, CancellationToken ct = default)
    {
        return await db.Vaults
            .AsNoTracking()
            .Where(v => names.Contains(v.Name)
                && (v.ProjectId == projectId
                    || (v.EnvironmentId != null && v.Environment!.ProjectId == projectId)
                    || (v.ProjectServerId != null && v.ProjectServer!.ProjectId == projectId)
                    || (v.ProjectId == null && v.EnvironmentId == null && v.ProjectServerId == null)))
            .Include(v => v.Secrets)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetVaultNamesAsync(int? projectId, List<int>? accessibleIds = null, CancellationToken ct = default)
    {
        var query = db.Vaults
            .AsNoTracking()
            .Where(v => v.ProjectId == projectId || v.ProjectId == null);
        if (accessibleIds is not null) query = query.Where(v => accessibleIds.Contains(v.Id));
        return await query
            .Select(v => v.Name)
            .OrderBy(n => n)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Vault?> FindVaultAsync(int id, CancellationToken ct = default)
    {
        return await db.Vaults.FindAsync([id], ct).ConfigureAwait(false);
    }

    public async Task AddVaultAsync(Vault vault, CancellationToken ct = default)
    {
        db.Vaults.Add(vault);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveVaultAsync(Vault vault, CancellationToken ct = default)
    {
        db.Vaults.Remove(vault);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<VaultSecret?> FindSecretAsync(int secretId, CancellationToken ct = default)
    {
        return await db.VaultSecrets.FindAsync([secretId], ct).ConfigureAwait(false);
    }

    public async Task AddSecretAsync(VaultSecret secret, CancellationToken ct = default)
    {
        db.VaultSecrets.Add(secret);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddSecretsRangeAsync(List<VaultSecret> secrets, CancellationToken ct = default)
    {
        db.VaultSecrets.AddRange(secrets);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveSecretAsync(VaultSecret secret, CancellationToken ct = default)
    {
        db.VaultSecrets.Remove(secret);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddSecretVersionAsync(VaultSecretVersion version, CancellationToken ct = default)
    {
        db.VaultSecretVersions.Add(version);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AddSecretVersionsRangeAsync(List<VaultSecretVersion> versions, CancellationToken ct = default)
    {
        db.VaultSecretVersions.AddRange(versions);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<VaultSecretVersion>> GetSecretVersionsAsync(int secretId, CancellationToken ct = default)
    {
        return await db.VaultSecretVersions
            .AsNoTracking()
            .Where(v => v.VaultSecretId == secretId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> PurgeHistoricalSecretVersionsAsync(
        int secretId,
        DateTime cutoffUtc,
        int maxCount,
        CancellationToken ct = default)
    {
        var currentVersion = await db.VaultSecretVersions
            .Where(version => version.VaultSecretId == secretId)
            .MaxAsync(version => (int?)version.Version, ct)
            .ConfigureAwait(false);
        if (currentVersion is null)
            return 0;

        var expiredIds = await db.VaultSecretVersions
            .Where(version =>
                version.VaultSecretId == secretId
                && version.Version != currentVersion.Value
                && version.ChangedAt < cutoffUtc)
            .OrderBy(version => version.ChangedAt)
            .ThenBy(version => version.Id)
            .Select(version => version.Id)
            .Take(Math.Clamp(maxCount, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
        if (expiredIds.Count == 0)
            return 0;

        var expired = db.VaultSecretVersions.Where(version => expiredIds.Contains(version.Id));
        if (db.Database.IsRelational())
            return await expired.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var rows = await expired.ToListAsync(ct).ConfigureAwait(false);
        db.VaultSecretVersions.RemoveRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> GetNextVersionAsync(int secretId, CancellationToken ct = default)
    {
        var maxVersion = await db.VaultSecretVersions
            .Where(v => v.VaultSecretId == secretId)
            .MaxAsync(v => (int?)v.Version, ct)
            .ConfigureAwait(false);

        return (maxVersion ?? 0) + 1;
    }

    public async Task<List<VaultSecret>> GetExpiringSecretsAsync(DateTime threshold, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.VaultSecrets
            .AsNoTracking()
            .Where(s => s.ExpiresAt != null && s.ExpiresAt <= threshold && s.ExpiresAt > now)
            .Include(s => s.Vault).ThenInclude(v => v.Environment)
            .Include(s => s.Vault).ThenInclude(v => v.ProjectServer)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
