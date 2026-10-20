// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.PortRegistry;

/// <summary>Backfills ServerApp.Port and ProjectServer.Port into the registry idempotently so
/// existing descriptors participate in availability checks. Conflicting claims are reported and
/// left with their current holder.</summary>
public static class IsolatedPortBackfill
{
    public static async Task RunAsync(
        AppDbContext db, IPortRegistryService registry, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(registry);

        var apps = await db.ServerApps
            .AsNoTracking()
            .Where(app => app.Port != null)
            .Select(app => new { app.Id, app.ServerId, app.Port, app.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var projectServers = await db.ProjectServers
            .AsNoTracking()
            .Where(projectServer => projectServer.Port != null && projectServer.ServerId != null)
            .Select(projectServer => new
            {
                projectServer.Id,
                projectServer.ServerId,
                projectServer.Port,
                projectServer.DisplayName,
                projectServer.ProjectId
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (apps.Count == 0 && projectServers.Count == 0) return;

        var carried = 0;
        var contested = 0;
        foreach (var app in apps)
        {
            if (await TrySyncAsync(
                    registry, app.ServerId, app.Port, PortRegistryService.ServerAppOwnerKey(app.Id),
                    app.Name, null, logger, ct).ConfigureAwait(false))
            {
                carried++;
            }
            else
            {
                contested++;
            }
        }

        foreach (var projectServer in projectServers)
        {
            if (await TrySyncAsync(
                    registry, projectServer.ServerId, projectServer.Port,
                    PortRegistryService.ProjectServerOwnerKey(projectServer.Id),
                    projectServer.DisplayName, projectServer.ProjectId, logger, ct).ConfigureAwait(false))
            {
                carried++;
            }
            else
            {
                contested++;
            }
        }

        logger.LogInformation(
            "Port registry backfill: {Carried} port(s) carried in, {Contested} left to their existing holder.",
            carried, contested);
    }

    /// <summary>
    /// One descriptor. A contested port is reported and skipped: the pre-existing holder is the one the
    /// registry already defends, and overwriting it at startup would silently steal a port from a
    /// running deployment.
    /// </summary>
    private static async Task<bool> TrySyncAsync(
        IPortRegistryService registry, int? serverId, int? port, string ownerKey, string ownerLabel,
        int? projectId, ILogger logger, CancellationToken ct)
    {
        try
        {
            await registry.SyncOwnedPortAsync(serverId, port, ownerKey, ownerLabel, projectId, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (ConflictException ex)
        {
            logger.LogWarning(
                "Port registry backfill left port {Port} of '{Owner}' alone: {Reason}",
                port, ownerLabel, ex.Message);
            return false;
        }
    }
}
