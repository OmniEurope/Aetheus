// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Everything a heartbeat writes, and the metric history it feeds.
///
/// Separated from <see cref="IServerRepository"/>, which had grown to carry both the server catalogue
/// and the whole inventory an agent reports on every beat. A caller that only reads servers has no
/// business seeing fourteen replace-the-world operations.
///
/// One beat is one transaction: the replacements stage changes and
/// <see cref="SaveChangesAsync"/> commits them once. That relies on this repository sharing the
/// caller's scoped <c>AppDbContext</c> - see the implementation for why a separate context would
/// break the heartbeat silently.
/// </summary>
public interface IServerHeartbeatRepository
{
    Task ReplaceServicesAsync(int serverId, List<ServiceInfo> services, CancellationToken ct = default);
    Task ReplaceDockerContainersAsync(int serverId, List<DockerContainer> containers, CancellationToken ct = default);
    /// <summary>Refreshes, in place and only where a value differs, the fields of the stored containers that move
    /// between two beats without the container changing (uptime text, CPU, memory).</summary>
    Task UpdateDockerContainerLiveFieldsAsync(int serverId, List<DockerContainer> reported, CancellationToken ct = default);
    Task ReplaceDockerImagesAsync(int serverId, List<DockerImage> images, CancellationToken ct = default);
    Task ReplaceDockerComposeStacksAsync(int serverId, List<DockerComposeStack> stacks, CancellationToken ct = default);
    Task ReplaceDockerNetworksAsync(int serverId, List<DockerNetwork> networks, CancellationToken ct = default);
    Task ReplaceDockerVolumesAsync(int serverId, List<DockerVolume> volumes, CancellationToken ct = default);
    Task ReplaceApacheDataAsync(
        int serverId, ApacheState? state, List<ApacheModule> modules, List<ApacheVirtualHost> vhosts,
        CancellationToken ct = default);
    Task ReplaceCertbotCertificatesAsync(int serverId, List<CertbotCertificate> certificates, CancellationToken ct = default);
    Task<DateTime?> GetCertbotRenewalCheckedAtAsync(int serverId, CancellationToken ct = default);
    Task ReplaceCertbotStateAsync(int serverId, CertbotState? state, CancellationToken ct = default);
    Task ReplaceMailDataAsync(int serverId, MailState? state, CancellationToken ct = default);
    Task ReplaceTeamspeakDataAsync(
        int serverId, TeamspeakState? state, List<TeamspeakChannel> channels,
        List<TeamspeakClient> clients, List<TeamspeakBan> bans,
        CancellationToken ct = default);
    Task ReplacePortsentryDataAsync(
        int serverId, PortsentryState? state, List<PortsentryBlockedIp> blockedIps, CancellationToken ct = default);
    Task ReplaceRkhunterDataAsync(
        int serverId, RkhunterState? state, List<RkhunterWarning> warnings, CancellationToken ct = default);
    Task ReplaceSecurityUpdatesDataAsync(int serverId, SecurityUpdatesState? state, CancellationToken ct = default);
    /// <summary>Moves only the check stamp of an unchanged security-updates report (no-op without a row).</summary>
    Task TouchSecurityUpdatesCheckedAtAsync(int serverId, DateTime checkedAt, CancellationToken ct = default);
    Task ReplaceFirewallDataAsync(int serverId, FirewallState? state, CancellationToken ct = default);

    /// <summary>Previous counters, read BEFORE a replacement so a beat can report what changed.</summary>
    Task<(int PreviousBlockedCount, int PreviousWarningCount)> GetSecurityCountersAsync(
        int serverId, CancellationToken ct = default);
    Task<SecurityUpdatesState?> GetSecurityUpdatesStateAsync(int serverId, CancellationToken ct = default);
    Task<FirewallState?> GetFirewallStateAsync(int serverId, CancellationToken ct = default);

    Task AddMetricAsync(ServerMetric metric, CancellationToken ct = default);
    Task<List<DateTime>> GetRecentMetricTimestampsAsync(int serverId, DateTime since, CancellationToken ct = default);
    Task<int> DeleteMetricsOlderThanAsync(DateTime cutoff, CancellationToken ct = default);

    /// <summary>Commits one beat's staged replacements as a single transaction.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
