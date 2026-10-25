// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Everything a heartbeat writes, and the metric history it feeds.
///
/// Split out of <see cref="ServerRepository"/>, which had grown to carry both the server catalogue
/// and the whole inventory an agent reports on every beat - two concerns with nothing in common
/// except the table prefix.
///
/// <b>The atomicity matters and is not accidental.</b> One heartbeat replaces a dozen inventories and
/// must land together. That depends on both repositories resolving the SAME scoped
/// <c>AppDbContext</c>, so that the caller commits everything once.
/// A future registration that gave this class its own context would break the heartbeat silently -
/// each replacement would commit on its own and a failure mid-beat would leave half an inventory.
///
/// Precision added after the 2026-08-21 audit (A360-55): sharing the context is what makes ONE
/// <c>SaveChanges</c> cover the whole beat, which is not the same as a database transaction. The
/// relational path uses <c>ExecuteDeleteAsync</c>, which executes immediately rather than being staged,
/// so the delete and the re-insert are not covered by a single commit unless the caller opened an
/// explicit transaction. The guarantee to rely on is "one save for the staged inserts", not "the whole
/// beat is transactional".
///
/// The <c>IsRelational()</c> branches are kept verbatim: <c>ExecuteDeleteAsync</c> is not supported by
/// the in-memory provider the unit tests use, so the fallback is what makes them exercise this code
/// at all rather than skip it.
/// </summary>
public sealed class ServerHeartbeatRepository(AppDbContext db) : IServerHeartbeatRepository
{
    public async Task<List<DateTime>> GetRecentMetricTimestampsAsync(int serverId, DateTime since, CancellationToken ct = default)
    {
        return await db.ServerMetrics
            .AsNoTracking()
            .Where(m => m.ServerId == serverId && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp)
            .Select(m => m.Timestamp)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public Task AddMetricAsync(ServerMetric metric, CancellationToken ct = default)
    {
        db.ServerMetrics.Add(metric);
        return Task.CompletedTask;
    }

    public async Task<(int PreviousBlockedCount, int PreviousWarningCount)> GetSecurityCountersAsync(int serverId, CancellationToken ct = default)
    {
        var blockedCount = await db.PortsentryStates
            .AsNoTracking()
            .Where(s => s.ServerId == serverId)
            .Select(s => (int?)s.BlockedCount)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? 0;

        var warningCount = await db.RkhunterStates
            .AsNoTracking()
            .Where(s => s.ServerId == serverId)
            .Select(s => (int?)s.WarningCount)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? 0;

        return (blockedCount, warningCount);
    }

    public async Task ReplaceServicesAsync(int serverId, List<ServiceInfo> services, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            await db.ServiceInfos.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.ServiceInfos.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.ServiceInfos.RemoveRange(old);
        }
        if (services.Count > 0)
            db.ServiceInfos.AddRange(services);
    }

    public async Task ReplaceDockerContainersAsync(int serverId, List<DockerContainer> containers, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            await db.DockerContainers.Where(c => c.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.DockerContainers.Where(c => c.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.DockerContainers.RemoveRange(old);
        }
        if (containers.Count > 0)
            db.DockerContainers.AddRange(containers);
    }

    public async Task ReplaceDockerImagesAsync(int serverId, List<DockerImage> images, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            await db.DockerImages.Where(i => i.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.DockerImages.Where(i => i.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.DockerImages.RemoveRange(old);
        }
        if (images.Count > 0)
            db.DockerImages.AddRange(images);
    }

    public async Task ReplaceDockerComposeStacksAsync(int serverId, List<DockerComposeStack> stacks, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            await db.DockerComposeStacks.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.DockerComposeStacks.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.DockerComposeStacks.RemoveRange(old);
        }
        if (stacks.Count > 0)
            db.DockerComposeStacks.AddRange(stacks);
    }

    public async Task ReplaceDockerNetworksAsync(int serverId, List<DockerNetwork> networks, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            await db.DockerNetworks.Where(n => n.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.DockerNetworks.Where(n => n.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.DockerNetworks.RemoveRange(old);
        }
        if (networks.Count > 0)
            db.DockerNetworks.AddRange(networks);
    }

    public async Task ReplaceDockerVolumesAsync(int serverId, List<DockerVolume> volumes, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            await db.DockerVolumes.Where(v => v.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.DockerVolumes.Where(v => v.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.DockerVolumes.RemoveRange(old);
        }
        if (volumes.Count > 0)
            db.DockerVolumes.AddRange(volumes);
    }

    public async Task ReplaceApacheDataAsync(int serverId, ApacheState? state, List<ApacheModule> modules, List<ApacheVirtualHost> vhosts, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.ApacheStates.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.ApacheModules.Where(m => m.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.ApacheVirtualHosts.Where(v => v.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var oldStates = await db.ApacheStates.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.ApacheStates.RemoveRange(oldStates);
            var oldModules = await db.ApacheModules.Where(m => m.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.ApacheModules.RemoveRange(oldModules);
            var oldVhosts = await db.ApacheVirtualHosts.Where(v => v.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.ApacheVirtualHosts.RemoveRange(oldVhosts);
        }

        if (state is not null)
            db.ApacheStates.Add(state);
        if (modules.Count > 0)
            db.ApacheModules.AddRange(modules);
        if (vhosts.Count > 0)
            db.ApacheVirtualHosts.AddRange(vhosts);
    }

    public async Task ReplaceCertbotCertificatesAsync(int serverId, List<CertbotCertificate> certificates, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            await db.CertbotCertificates.Where(c => c.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.CertbotCertificates.Where(c => c.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.CertbotCertificates.RemoveRange(old);
        }
        if (certificates.Count > 0)
            db.CertbotCertificates.AddRange(certificates);
    }

    public Task<DateTime?> GetCertbotRenewalCheckedAtAsync(int serverId, CancellationToken ct = default) =>
        db.CertbotStates.AsNoTracking()
            .Where(s => s.ServerId == serverId)
            .Select(s => s.RenewalCheckedAt)
            .FirstOrDefaultAsync(ct);

    public async Task ReplaceCertbotStateAsync(int serverId, CertbotState? state, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
            await db.CertbotStates.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.CertbotStates.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.CertbotStates.RemoveRange(old);
        }

        if (state is not null)
            db.CertbotStates.Add(state);
    }

    public async Task ReplaceMailDataAsync(int serverId, MailState? state, CancellationToken ct = default)
    {
        // The row is rewritten on each change; the quota resync mark must survive that, and clears while the
        // helper is older than the quota version so the next upgrade resyncs again.
        var resyncedAt = await db.MailStates.AsNoTracking().Where(s => s.ServerId == serverId)
            .Select(s => s.QuotaResyncedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (state is not null)
            state.QuotaResyncedAt = state.HelperVersion >= MailHelperVersions.QuotaMinimum ? resyncedAt : null;
        if (db.Database.IsRelational())
            await db.MailStates.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var old = await db.MailStates.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.MailStates.RemoveRange(old);
        }

        if (state is not null)
            db.MailStates.Add(state);
    }

    public async Task ReplaceTeamspeakDataAsync(
        int serverId, TeamspeakState? state, List<TeamspeakChannel> channels,
        List<TeamspeakClient> clients, List<TeamspeakBan> bans,
        CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.TeamspeakStates.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.TeamspeakChannels.Where(c => c.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.TeamspeakClients.Where(client => client.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.TeamspeakBans.Where(ban => ban.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var oldStates = await db.TeamspeakStates.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.TeamspeakStates.RemoveRange(oldStates);
            var oldChannels = await db.TeamspeakChannels.Where(c => c.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.TeamspeakChannels.RemoveRange(oldChannels);
            var oldClients = await db.TeamspeakClients.Where(client => client.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.TeamspeakClients.RemoveRange(oldClients);
            var oldBans = await db.TeamspeakBans.Where(ban => ban.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.TeamspeakBans.RemoveRange(oldBans);
        }

        if (state is not null)
            db.TeamspeakStates.Add(state);
        if (channels.Count > 0)
            db.TeamspeakChannels.AddRange(channels);
        if (clients.Count > 0)
            db.TeamspeakClients.AddRange(clients);
        if (bans.Count > 0)
            db.TeamspeakBans.AddRange(bans);
    }

    public async Task ReplacePortsentryDataAsync(int serverId, PortsentryState? state, List<PortsentryBlockedIp> blockedIps, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.PortsentryStates.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.PortsentryBlockedIps.Where(ip => ip.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var oldStates = await db.PortsentryStates.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.PortsentryStates.RemoveRange(oldStates);
            var oldIps = await db.PortsentryBlockedIps.Where(ip => ip.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.PortsentryBlockedIps.RemoveRange(oldIps);
        }

        if (state is not null)
            db.PortsentryStates.Add(state);
        if (blockedIps.Count > 0)
            db.PortsentryBlockedIps.AddRange(blockedIps);
    }

    /// <summary>Update reported RKHunter state in place to preserve the operator schedule and
    /// scheduler timestamp, which the heartbeat does not carry. Remove the row when RKHunter is
    /// absent from the report.</summary>
    public async Task ReplaceRkhunterDataAsync(int serverId, RkhunterState? state, List<RkhunterWarning> warnings, CancellationToken ct = default)
    {
        var existing = await db.RkhunterStates
            .Where(s => s.ServerId == serverId)
            .ToListAsync(ct).ConfigureAwait(false);
        var kept = state is null ? null : existing.FirstOrDefault();
        db.RkhunterStates.RemoveRange(existing.Where(row => !ReferenceEquals(row, kept)));
        if (state is not null && kept is null)
            db.RkhunterStates.Add(state);
        else if (state is not null && kept is not null)
        {
            kept.Version = state.Version;
            kept.DatabaseVersion = state.DatabaseVersion;
            kept.LastScanTime = state.LastScanTime;
            kept.LastScanStatus = state.LastScanStatus;
            kept.WarningCount = state.WarningCount;
            kept.DatabaseLastUpdated = state.DatabaseLastUpdated;
        }

        if (db.Database.IsRelational())
            await db.RkhunterWarnings.Where(w => w.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
        {
            var oldWarnings = await db.RkhunterWarnings.Where(w => w.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.RkhunterWarnings.RemoveRange(oldWarnings);
        }
        if (warnings.Count > 0)
            db.RkhunterWarnings.AddRange(warnings);
    }

    public async Task ReplaceSecurityUpdatesDataAsync(int serverId, SecurityUpdatesState? state, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.SecurityUpdatesStates.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var old = await db.SecurityUpdatesStates.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.SecurityUpdatesStates.RemoveRange(old);
        }

        if (state is not null)
            db.SecurityUpdatesStates.Add(state);
    }

    /// <summary> A beat whose security-updates report did not change still proves a fresh check, so
    /// it moves <see cref="SecurityUpdatesState.CheckedAt"/> alone (one UPDATE, no delete and
    /// reinsert). No-op when the server keeps no security-updates row. </summary>
    public async Task TouchSecurityUpdatesCheckedAtAsync(int serverId, DateTime checkedAt, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.SecurityUpdatesStates
                .Where(s => s.ServerId == serverId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.CheckedAt, checkedAt)
                    .SetProperty(s => s.LastUpdated, checkedAt), ct)
                .ConfigureAwait(false);
        }
        else
        {
            var rows = await db.SecurityUpdatesStates.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
                row.CheckedAt = checkedAt;
        }
    }

    public async Task<SecurityUpdatesState?> GetSecurityUpdatesStateAsync(int serverId, CancellationToken ct = default)
        => await db.SecurityUpdatesStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ServerId == serverId, ct).ConfigureAwait(false);

    public async Task ReplaceFirewallDataAsync(int serverId, FirewallState? state, CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            await db.FirewallStates.Where(s => s.ServerId == serverId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var old = await db.FirewallStates.Where(s => s.ServerId == serverId).ToListAsync(ct).ConfigureAwait(false);
            db.FirewallStates.RemoveRange(old);
        }

        if (state is not null)
            db.FirewallStates.Add(state);
    }

    public async Task<FirewallState?> GetFirewallStateAsync(int serverId, CancellationToken ct = default)
        => await db.FirewallStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ServerId == serverId, ct).ConfigureAwait(false);

    public async Task<int> DeleteMetricsOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        // Set-based delete. The old load-all → RemoveRange → SaveChanges path
        // materialised the entire expired metrics table into memory every
        // cleanup cycle (heartbeats accrue fast across the fleet).
        if (db.Database.IsRelational())
        {
            return await db.ServerMetrics
                .Where(m => m.Timestamp < cutoff)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        // InMemory fallback - ExecuteDelete not supported
        var expired = await db.ServerMetrics
            .Where(m => m.Timestamp < cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
        db.ServerMetrics.RemoveRange(expired);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return expired.Count;
    }

    /// <summary>
    /// Commits the staged replacements. Called by the heartbeat processor once, after every
    /// inventory has been staged, so one beat is one transaction.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
}
