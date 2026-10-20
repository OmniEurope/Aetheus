// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// Service-management actions on a remote server (start/stop/restart/install/uninstall a
/// systemd unit, Docker container or Windows Service, plus journal-style log retrieval).
/// Each call enqueues a <c>ServerTask</c> for the agent to execute. Split out from
/// <see cref="IServerService"/> for ISP.
/// </summary>
public interface IServerServiceManagementService
{
    Task<int> ExecuteServiceActionAsync(int serverId, ServiceActionRequest request, CancellationToken ct = default);
    Task<int> InstallServiceAsync(int serverId, string serviceName, CancellationToken ct = default);
    Task<int> UninstallServiceAsync(int serverId, string serviceName, CancellationToken ct = default);
    Task<int> CreateServiceLogsTaskAsync(int serverId, string serviceName, int lines, bool follow, CancellationToken ct = default);

    /// <summary>ADR-024 4.1: enqueue a system package upgrade (dry-run preview or consented apply).</summary>
    Task<int> UpgradeSystemAsync(int serverId, bool dryRun, CancellationToken ct = default);

    /// <summary>Recette R3-59: enqueue the read-only list of every installed package (no grant, nothing changed).</summary>
    Task<int> ListInstalledPackagesAsync(int serverId, CancellationToken ct = default);

    /// <summary>ADR-024 4.1: read the server's last reported patch status (pending counts + packages).</summary>
    Task<ServerSecurityUpdatesDto> GetSecurityUpdatesAsync(int serverId, CancellationToken ct = default);

    // --- ADR-024 4.2: firewall (ufw) ---
    Task<int> FirewallAllowAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default);
    Task<int> FirewallDenyAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default);
    Task<int> FirewallDeleteRuleAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default);
    Task<int> FirewallToggleAsync(int serverId, bool enabled, CancellationToken ct = default);
    Task<ServerFirewallDto> GetFirewallAsync(int serverId, CancellationToken ct = default);
}
