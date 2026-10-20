// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

public class ServerRetirementRepository(AppDbContext db) : IServerRetirementRepository
{
    // Every query here must see retired rows: that is the whole point of this repository.
    private IQueryable<Server> ServersIncludingRetired =>
        db.Servers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired]);

    public Task<Server?> FindServerIncludingRetiredAsync(int id, CancellationToken ct = default) =>
        ServersIncludingRetired.FirstOrDefaultAsync(server => server.Id == id, ct);

    public async Task RetireAsync(Server server, DateTime retiredAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.DeletedAt = retiredAt;
        server.Status = ServerStatus.Offline;

        // Revoked, not deleted: the old agent must stop authenticating now, and the token history
        // stays inspectable. Loaded and flipped (not ExecuteUpdate) so it commits in the same save as
        // DeletedAt on every provider.
        var activeTokens = await db.ServerTokens
            .Where(token => token.ServerId == server.Id && !token.IsRevoked)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var token in activeTokens)
            token.IsRevoked = true;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(List<RetiredServerDto> Items, int TotalCount)> GetRetiredServersPagedAsync(
        string? search, string? sortBy, bool sortDescending, int page, int pageSize, List<int>? accessibleIds,
        CancellationToken ct = default, IReadOnlyList<GridFilter>? filters = null)
    {
        var query = ServersIncludingRetired.AsNoTracking().Where(server => server.DeletedAt != null);
        if (accessibleIds is not null)
            query = query.Where(server => accessibleIds.Contains(server.Id));
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(server => server.Name.Contains(search) || server.Hostname.Contains(search));
        // The header filters, after the access scope and before the count.
        query = RetiredServerListQuery.Columns.ApplyFilters(query, filters);

        var totalCount = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await SortRetired(query, sortBy, sortDescending)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(server => new RetiredServerDto
            {
                Id = server.Id,
                Name = server.Name,
                Hostname = server.Hostname,
                OsDescription = server.OsDescription,
                RetiredAt = server.DeletedAt!.Value,
                LastHeartbeat = server.LastHeartbeat,
                OrganizationId = server.OrganizationId,
                HasMachineIdentity = server.MachineIdHash != null
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, totalCount);
    }

    // Most recently retired first unless the grid asks otherwise; Id keeps paging deterministic.
    private static IQueryable<Server> SortRetired(IQueryable<Server> query, string? sortBy, bool descending) =>
        sortBy?.ToLowerInvariant() switch
        {
            "name" => (descending ? query.OrderByDescending(server => server.Name) : query.OrderBy(server => server.Name)).ThenBy(server => server.Id),
            "hostname" => (descending ? query.OrderByDescending(server => server.Hostname) : query.OrderBy(server => server.Hostname)).ThenBy(server => server.Id),
            "retiredat" when !descending => query.OrderBy(server => server.DeletedAt).ThenBy(server => server.Id),
            _ => query.OrderByDescending(server => server.DeletedAt).ThenBy(server => server.Id)
        };

    public async Task<int> PurgeServerAsync(Server server, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var id = server.Id;

        // Relying solely on FK CASCADE was too slow on servers with months of
        // metrics history - the single cascading transaction took long enough
        // that the browser surfaced it as a 'NetworkError' (the fetch gave up
        // before the response came back). On relational providers we set-delete
        // the heavy leaf tables first; the remaining small relations cascade.
        // InMemory (used by unit tests) cascades tracked entities via EF - it
        // does not support ExecuteDelete.
        if (db.Database.IsRelational())
        {
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));

            // Atomicity: the leaf set-deletes and the final server delete must all
            // commit together, so a mid-sequence failure can't leave orphaned rows.
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Restrict-FK: Vault → ProjectServer → Server - delete leaf-to-root.
            var psIds = await db.ProjectServers.Where(ps => ps.ServerId == id).Select(ps => ps.Id).ToListAsync(ct).ConfigureAwait(false);
            if (psIds.Count > 0)
            {
                await db.VaultSecretVersions.Where(v => v.VaultSecret!.Vault!.ProjectServerId != null && psIds.Contains(v.VaultSecret.Vault.ProjectServerId!.Value)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.VaultSecrets.Where(v => v.Vault.ProjectServerId != null && psIds.Contains(v.Vault.ProjectServerId!.Value)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.Vaults.Where(v => v.ProjectServerId != null && psIds.Contains(v.ProjectServerId!.Value)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.EnvironmentProjectServers.Where(x => psIds.Contains(x.ProjectServerId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.ProjectServers.Where(ps => ps.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
            // Restrict-FK: BackupPolicy → Server. A policy targets this host, so it cannot outlive
            // it; its runs cascade from the policy. Deleted with the server rather than left to fail
            // the whole purge on the FK (the caller audits how many went).
            var backupPolicies = await db.BackupPolicies.Where(policy => policy.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.ServerMetrics.Where(m => m.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.ServiceInfos.Where(s => s.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.DockerContainers.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.DockerImages.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.DockerNetworks.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.DockerVolumes.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.DockerComposeStacks.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.ApacheStates.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.ApacheModules.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.ApacheVirtualHosts.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CertbotCertificates.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CertbotStates.Where(x => x.ServerId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);

            db.Servers.Remove(server);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return backupPolicies;
        }

        var trackedPolicies = await db.BackupPolicies
            .Where(policy => policy.ServerId == id)
            .Include(policy => policy.Runs)
            .ToListAsync(ct).ConfigureAwait(false);
        db.BackupPolicies.RemoveRange(trackedPolicies);
        db.Servers.Remove(server);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return trackedPolicies.Count;
    }
}
