// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// systemd/Docker/Windows service lifecycle actions (start/stop/restart), install/uninstall, and
/// journal-log task creation. Extracted from the former <c>ServerService.ServiceManagement.cs</c>
/// partial into a real collaborator that <see cref="ServerService"/> delegates to.
/// </summary>
internal sealed class ServerServiceManager(
    IServerRepository repo,
    IServerHeartbeatRepository heartbeatRepo,
    IAuditService audit,
    ITaskService taskService)
{
    public async Task<int> ExecuteServiceActionAsync(int serverId, ServiceActionRequest request, CancellationToken ct = default)
    {
        // Defence-in-depth (#4 / #24): the DTO RegularExpression already rejects bad names, but
        // re-validate here in case this method is ever called from a non-controller path.
        if (!OperationTargetValidator.ServiceNameRegex().IsMatch(request.ServiceName))
            throw new BadRequestException("Service name contains invalid characters.");

        await EnsureServerOnlineAsync(serverId, ct).ConfigureAwait(false);

        var serviceType = await repo.GetServiceTypeAsync(serverId, request.ServiceName, ct).ConfigureAwait(false)
            ?? (ServerDataMapper.WellKnownManageableServices.TryGetValue(request.ServiceName, out var wkType) ? wkType : (ServiceType?)null)
            ?? throw new NotFoundException($"Service '{request.ServiceName}' not found on server {serverId}");

        // systemd units need root: a bare `systemctl start <unit>` shell task runs as the unprivileged
        // agent user and dies with polkit "Access denied". Route it through the typed ServiceOperation
        // instead - the agent executes `sudo -n /bin/systemctl <verb> <unit>` against the argv-exact
        // AETHEUS_SYSTEMCTL sudoers allow-list. Docker/Windows keep their shell command (docker via the
        // docker group, `net` via the Windows service path), which don't need sudo.
        ServerTask task;
        if (serviceType == ServiceType.Systemd)
        {
            var operation = request.Action switch
            {
                ServiceAction.Start => OperationKind.ServiceStart,
                ServiceAction.Stop => OperationKind.ServiceStop,
                ServiceAction.Restart => OperationKind.ServiceRestart,
                _ => throw new BadRequestException($"Unsupported service action: {request.Action}")
            };
            task = ServerTaskFactory.Operation(serverId, $"Service {request.Action} - {request.ServiceName}", operation, request.ServiceName, 30);
        }
        else
        {
            var (command, executor) = BuildServiceCommand(request.Action, request.ServiceName, serviceType);
            task = ServerTaskFactory.Create(serverId, $"Service {request.Action} - {request.ServiceName}", command, executor, 30);
        }
        await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);

        await audit.LogAsync($"Service{request.Action}", "Service", serverId,
            request.ServiceName, ct).ConfigureAwait(false);

        return task.Id;
    }

    // A queued task only runs if an agent on the target server is polling for it. Refuse to enqueue
    // one for an offline server so it does not sit Pending forever with no logs (the apache2-install
    // confusion, where the action targeted a stale offline VPS-sim server).
    private async Task EnsureServerOnlineAsync(int serverId, CancellationToken ct)
    {
        var server = await repo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        EnsureOnline(server);
    }

    private static void EnsureOnline(Server server)
    {
        if (server.Status != ServerStatus.Online)
            throw new ConflictException(
                "The target server's agent is offline, so this action cannot run. Reconnect the agent and try again.");
    }

    internal static (string Command, ExecutorType Executor) BuildServiceCommand(
        ServiceAction action, string serviceName, ServiceType serviceType)
    {
        var verb = action.ToString().ToLowerInvariant();
        return serviceType switch
        {
            ServiceType.Systemd => ($"systemctl {verb} {serviceName}", ExecutorType.Shell),
            ServiceType.Docker => ($"docker {verb} {serviceName}", ExecutorType.Docker),
            ServiceType.WindowsService => action switch
            {
                ServiceAction.Restart => ($"net stop \"{serviceName}\" && net start \"{serviceName}\"", ExecutorType.Shell),
                _ => ($"net {verb} \"{serviceName}\"", ExecutorType.Shell)
            },
            _ => throw new BadRequestException($"Unsupported service type: {serviceType}")
        };
    }

    public async Task<int> InstallServiceAsync(int serverId, string serviceName, CancellationToken ct = default)
        => await DispatchPackageOperationAsync(
            serverId, serviceName, OperationKind.ServiceInstall, "Install", "ServiceInstall", ct).ConfigureAwait(false);

    public async Task<int> UninstallServiceAsync(int serverId, string serviceName, CancellationToken ct = default)
        => await DispatchPackageOperationAsync(
            serverId, serviceName, OperationKind.ServiceUninstall, "Uninstall", "ServiceUninstall", ct).ConfigureAwait(false);

    internal static bool IsMailStackPackage(string service) =>
        string.Equals(service, "postfix", StringComparison.OrdinalIgnoreCase)
        || string.Equals(service, "dovecot", StringComparison.OrdinalIgnoreCase);

    // S-FEAT-W8KN: install/uninstall a managed package via the typed controlled-sudo operation. The old
    // path enqueued a bare `apt-get install` shell task that always failed for the non-root agent (no
    // sudoers grant); this dispatches OperationKind.ServiceInstall/Uninstall, which the agent runs as
    // `sudo -n apt-get … -y <pkg>` against the argv-exact aetheus-package allow-list. The UI sends a
    // service key (docker/mysql/mongod); we resolve it to the real apt package name (docker.io/
    // mysql-server/mongodb-org) so apt can actually locate it. Uninstall is refused for protected
    // packages (databases, ufw/fail2ban, docker). Gated on the server's reported package-manage
    // capability so we never queue an action the agent cannot perform.
    private async Task<int> DispatchPackageOperationAsync(
        int serverId, string service, OperationKind operation, string verb, string auditAction, CancellationToken ct)
    {
        var aptPackage = ManageablePackages.AptPackageFor(service)
            ?? throw new BadRequestException($"Unknown or unsupported package: {service}");

        if (operation == OperationKind.ServiceUninstall && !ManageablePackages.IsRemovablePackage(aptPackage))
            throw new BadRequestException(
                $"Package '{service}' is protected and cannot be uninstalled (it holds data or underpins " +
                "the server's security posture / pipeline runner).");

        var server = await repo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        // Recette R3-24/R3-25/R3-43: Postfix and Dovecot of a stack Aetheus set up are one unit with OpenDKIM,
        // rspamd and their configuration. Installing or removing one package here left a half stack (pop3
        // configured without its package, OpenDKIM still listening); the Mail page reinstalls or purges it whole.
        if (IsMailStackPackage(service) && await repo.IsMailStackManagedAsync(serverId, ct).ConfigureAwait(false))
            throw new BadRequestException(
                $"'{service}' belongs to the mail stack Aetheus manages on this server. Reinstall or remove the " +
                "whole stack from the server's Mail page, so no part of it is left behind.");
        EnsureOnline(server);
        if (!server.PackageManagementAvailable)
            throw new BadRequestException(
                "Package management is not enabled on this server. Re-run the agent installer with the " +
                "server-management module (or --enable-package-manage) to grant the controlled-sudo capability.");

        var task = ServerTaskFactory.Operation(serverId, $"{verb} - {service}", operation, aptPackage, 300);
        await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);

        await audit.LogAsync(auditAction, "Service", serverId, aptPackage, ct).ConfigureAwait(false);

        return task.Id;
    }

    // Recette R3-59: queue the read-only list of every installed package. No package grant (nothing is
    // installed or removed, the agent only reads the package database), only an online Linux agent that
    // publishes the package.inventory capability.
    public async Task<int> ListInstalledPackagesAsync(int serverId, CancellationToken ct = default)
    {
        var server = await repo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        EnsureOnline(server);
        if (server.OsType == OsType.Windows)
            throw new BadRequestException("The installed package list is read on Linux servers only.");
        if (server.AgentCapabilitiesJson?.Contains($"\"{AgentCapabilities.PackageInventory}\"", StringComparison.Ordinal) != true)
            throw new BadRequestException(
                $"The agent on '{server.Name}' does not publish the '{AgentCapabilities.PackageInventory}' capability: " +
                "update the agent to list the installed packages.");

        var task = ServerTaskFactory.Operation(serverId, InstalledPackages.TaskName, OperationKind.PackageInventory, "-", 120);
        await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
        return task.Id;
    }

    // ADR-024 4.1: enqueue a whole-box apt-get upgrade. Two modes ride in AETHEUS_PATCH_DRY_RUN: a
    // non-mutating preview (dry-run) or a consented apply. Gated on the reported patch-manage capability
    // so we never queue an apply the agent cannot perform; the agent still runs the dry-run first and
    // aborts honestly on any critical package. A generous 900s timeout covers a large upgrade set.
    public async Task<int> UpgradeSystemAsync(int serverId, bool dryRun, CancellationToken ct = default)
    {
        var server = await repo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        EnsureOnline(server);
        if (!server.PatchManagementAvailable)
            throw new BadRequestException(
                "Patch management is not enabled on this server. Re-run the agent installer with "
                + "--enable-patch-manage to grant the controlled-sudo apt-get upgrade capability.");

        var env = new Dictionary<string, string> { [PatchingConstants.DryRunEnvVar] = dryRun ? "1" : "0" };
        var name = dryRun ? "System update check (dry-run)" : "System package upgrade";
        var task = ServerTaskFactory.Operation(serverId, name, OperationKind.SystemPackageUpgrade, "-", env, 900);
        await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
        await audit.LogAsync(dryRun ? "SystemUpgradeDryRun" : "SystemUpgrade", "Server", serverId, null, ct).ConfigureAwait(false);
        return task.Id;
    }

    public async Task<ServerSecurityUpdatesDto> GetSecurityUpdatesAsync(int serverId, CancellationToken ct = default)
    {
        var server = await repo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        var state = await heartbeatRepo.GetSecurityUpdatesStateAsync(serverId, ct).ConfigureAwait(false);

        var updates = state?.UpdatesJson is { } json && !string.IsNullOrEmpty(json)
            ? JsonSerializer.Deserialize<List<PendingUpdateDto>>(json) ?? []
            : [];

        return new ServerSecurityUpdatesDto
        {
            // No-fake: "Known" is true only when a dry-run probe actually succeeded.
            Known = state is { Probed: true },
            PackageManagerPresent = state is not null,
            PatchManagementAvailable = server.PatchManagementAvailable,
            PendingTotal = state?.PendingTotal ?? 0,
            PendingSecurity = state?.PendingSecurity ?? 0,
            CheckedAt = state?.CheckedAt,
            Updates = updates
        };
    }

    // --- ADR-024 4.2: firewall (ufw) via the root-owned aetheus-firewall helper ---

    public Task<int> FirewallAllowAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => DispatchFirewallRuleAsync(serverId, OperationKind.FirewallAllow, request, ct);

    public Task<int> FirewallDenyAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => DispatchFirewallRuleAsync(serverId, OperationKind.FirewallDeny, request, ct);

    public Task<int> FirewallDeleteRuleAsync(int serverId, FirewallRuleRequest request, CancellationToken ct = default)
        => DispatchFirewallRuleAsync(serverId, OperationKind.FirewallDeleteRule, request, ct);

    private async Task<int> DispatchFirewallRuleAsync(int serverId, OperationKind operation, FirewallRuleRequest request, CancellationToken ct)
    {
        if (!FirewallValidation.IsValidProtocol(request.Protocol))
            throw new BadRequestException("Invalid protocol (allowed: tcp, udp).");
        if (!FirewallValidation.IsValidSource(request.Source))
            throw new BadRequestException("Invalid source (expected a CIDR, an IP, or 'any').");
        // Anti-lockout (LOGIC guard, defence in depth with the agent helper): never close the admin port.
        if (FirewallLockoutGuard.IsLockoutRisk(operation, request.Port))
            throw new BadRequestException(
                $"Refused: closing the administration port {request.Port} would lock you out of SSH.");

        var server = await GetFirewallManagedServerAsync(serverId, ct).ConfigureAwait(false);

        var env = new Dictionary<string, string>
        {
            [FirewallConstants.ProtoEnvVar] = request.Protocol.ToLowerInvariant(),
            [FirewallConstants.SourceEnvVar] = request.Source
        };
        var verb = operation switch
        {
            OperationKind.FirewallAllow => "allow",
            OperationKind.FirewallDeny => "deny",
            _ => "delete"
        };
        var task = ServerTaskFactory.Operation(serverId, $"Firewall {verb} {request.Port}/{request.Protocol}",
            operation, request.Port.ToString(CultureInfo.InvariantCulture), env, 60);
        await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
        await audit.LogAsync($"Firewall{operation}", "Firewall", serverId,
            $"{request.Port}/{request.Protocol} from {request.Source}", ct).ConfigureAwait(false);
        return task.Id;
    }

    public async Task<int> FirewallToggleAsync(int serverId, bool enabled, CancellationToken ct = default)
    {
        var server = await GetFirewallManagedServerAsync(serverId, ct).ConfigureAwait(false);

        var target = enabled ? "enable" : "disable";
        var task = ServerTaskFactory.Operation(serverId, $"Firewall {target}", OperationKind.FirewallSetEnabled, target, 60);
        await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
        await audit.LogAsync(enabled ? "FirewallEnable" : "FirewallDisable", "Firewall", serverId, null, ct).ConfigureAwait(false);
        return task.Id;
    }

    private async Task<Server> GetFirewallManagedServerAsync(int serverId, CancellationToken ct)
    {
        var server = await repo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        EnsureOnline(server);
        if (!server.FirewallManagementAvailable)
            throw new BadRequestException(
                "Firewall management is not enabled on this server. Re-run the agent installer with "
                + "--enable-firewall-manage to grant the controlled-sudo capability.");
        return server;
    }

    public async Task<ServerFirewallDto> GetFirewallAsync(int serverId, CancellationToken ct = default)
    {
        var server = await repo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        var state = await heartbeatRepo.GetFirewallStateAsync(serverId, ct).ConfigureAwait(false);

        var rules = state?.RulesJson is { } json && !string.IsNullOrEmpty(json)
            ? JsonSerializer.Deserialize<List<FirewallRuleDto>>(json) ?? []
            : [];

        return new ServerFirewallDto
        {
            Installed = state?.Installed ?? false,
            Active = state?.Active ?? false,
            StatusKnown = state?.StatusKnown ?? false,
            CollectionDiagnostics = state?.CollectionDiagnostics ?? string.Empty,
            FirewallManagementAvailable = server.FirewallManagementAvailable,
            Rules = rules
        };
    }

    public async Task<int> CreateServiceLogsTaskAsync(int serverId, string serviceName, int lines, bool follow, CancellationToken ct = default)
    {
        if (!OperationTargetValidator.ServiceNameRegex().IsMatch(serviceName))
            throw new BadRequestException("Service name contains invalid characters.");

        await EnsureServerOnlineAsync(serverId, ct).ConfigureAwait(false);

        var sanitizedLines = Math.Clamp(lines, 1, 10000);
        // Typed op: unit in the target, line count / follow flag in env. The agent builds the journalctl
        // argv (no shell interpolation) - replaces the former shell-string logs task.
        var env = new Dictionary<string, string>
        {
            [ServiceLogsEnv.Lines] = sanitizedLines.ToString(CultureInfo.InvariantCulture),
            [ServiceLogsEnv.Follow] = follow ? "true" : "false"
        };
        var task = ServerTaskFactory.Operation(serverId, $"Logs - {serviceName}", OperationKind.ServiceGetLogs, serviceName, env, follow ? 60 : 30);

        await repo.AddTaskAsync(task, ct).ConfigureAwait(false);
        await taskService.NotifyTaskQueuedAsync(task, ct: ct).ConfigureAwait(false);
        await audit.LogAsync("ServiceLogs", "Service", serverId, serviceName, ct).ConfigureAwait(false);
        return task.Id;
    }
}
