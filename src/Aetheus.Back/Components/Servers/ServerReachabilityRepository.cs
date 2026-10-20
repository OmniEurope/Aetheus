// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

public sealed class ServerReachabilityRepository(AppDbContext db) : IServerReachabilityRepository
{
    public async Task<List<Server>> GetOfflineServersDueForProbeAsync(DateTime probedBefore, CancellationToken ct = default)
    {
        return await db.Servers
            .Where(s => s.Status == ServerStatus.Offline
                && s.LastHeartbeat > DateTime.MinValue
                && (s.ExternalProbeAt == null
                    || s.ExternalProbeAt < probedBefore
                    || s.ExternalProbeAt < s.LastHeartbeat))
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordAsync(int serverId, ServerReachability result, string? target, DateTime probedAt, CancellationToken ct = default)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == serverId, ct).ConfigureAwait(false);
        if (server is null) return;
        server.ExternalProbe = result;
        server.ExternalProbeAt = probedAt;
        server.ExternalProbeTarget = target is { Length: > 128 } ? target[..128] : target;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
