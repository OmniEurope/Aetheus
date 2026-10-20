// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ServerServiceManagementTests
{
    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly IServerHeartbeatRepository _heartbeatRepoMock = Substitute.For<IServerHeartbeatRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly ITaskService _taskService = Substitute.For<ITaskService>();
    private readonly ServerService _sut;

    public ServerServiceManagementTests()
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        var clients = Substitute.For<IHubClients>();
        var proxy = Substitute.For<IClientProxy>();
        clients.All.Returns(proxy);
        clients.Group(Arg.Any<string>()).Returns(proxy);
        clients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(proxy);
        hub.Clients.Returns(clients);
        alertHub.Clients.Returns(clients);

        _sut = new ServerService(_repo, _heartbeatRepoMock, hub, alertHub, _audit, _taskService,
            Options.Create(new BackgroundServicesOptions()), TimeProvider.System,
            Substitute.For<IDbTransactionScope>());

        // Default: server 1 exists and its agent is online, so the agent-online guard passes.
        // Capability- and offline-specific tests override this stub.
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Online });
    }

    // --- ExecuteServiceActionAsync ---

    [Fact]
    public async Task ExecuteServiceAction_InvalidName_ThrowsBadRequest()
        => await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ExecuteServiceActionAsync(1, new ServiceActionRequest { ServiceName = "bad name!", Action = ServiceAction.Start }, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task ExecuteServiceAction_UnknownService_ThrowsNotFound()
    {
        _repo.GetServiceTypeAsync(1, "ghostservice", Arg.Any<CancellationToken>()).Returns((ServiceType?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.ExecuteServiceActionAsync(1, new ServiceActionRequest { ServiceName = "ghostservice", Action = ServiceAction.Start }, ct: TestContext.Current.CancellationToken));
    }

    // Systemd services need root → route through the typed ServiceRestart operation (agent runs
    // `sudo -n /bin/systemctl restart <unit>`), NOT a bare shell `systemctl` task that 'Access denied's
    // for the non-root agent.
    [Fact]
    public async Task ExecuteServiceAction_SystemdFromRepo_QueuesTypedOperationAndAudits()
    {
        _repo.GetServiceTypeAsync(1, "customsvc", Arg.Any<CancellationToken>()).Returns(ServiceType.Systemd);

        await _sut.ExecuteServiceActionAsync(1, new ServiceActionRequest { ServiceName = "customsvc", Action = ServiceAction.Restart }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.Operation == OperationKind.ServiceRestart && t.Command == "customsvc"),
            Arg.Any<CancellationToken>());
        await _taskService.Received(1).NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ServiceRestart", "Service", 1, "customsvc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteServiceAction_WellKnownDocker_ResolvesAndQueues()
    {
        _repo.GetServiceTypeAsync(1, "docker", Arg.Any<CancellationToken>()).Returns((ServiceType?)null);

        await _sut.ExecuteServiceActionAsync(1, new ServiceActionRequest { ServiceName = "docker", Action = ServiceAction.Stop }, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    // The agent must be polling to claim a task; queuing one for an offline server would strand it
    // Pending forever (the apache2-install confusion). Refused with a 409 before anything is queued.
    [Fact]
    public async Task ExecuteServiceAction_OfflineServer_ThrowsConflict_AndQueuesNothing()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Offline });

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.ExecuteServiceActionAsync(1, new ServiceActionRequest { ServiceName = "customsvc", Action = ServiceAction.Restart }, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallService_OfflineServer_ThrowsConflict_AndQueuesNothing()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Offline, PackageManagementAvailable = true });

        await Assert.ThrowsAsync<ConflictException>(() => _sut.InstallServiceAsync(1, "nginx", ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    // --- Install / Uninstall ---

    [Fact]
    public async Task InstallService_UnknownName_ThrowsBadRequest()
        => await Assert.ThrowsAsync<BadRequestException>(() => _sut.InstallServiceAsync(1, "totallyunknown", ct: TestContext.Current.CancellationToken));

    // S-FEAT-W8KN: install is gated on the server's reported package-manage capability.
    [Fact]
    public async Task InstallService_WithoutPackageManageCapability_ThrowsBadRequest()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Online, PackageManagementAvailable = false });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.InstallServiceAsync(1, "nginx", ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _taskService.DidNotReceive().NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallService_WellKnown_QueuesTypedOperationAndAudits()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Online, PackageManagementAvailable = true });

        await _sut.InstallServiceAsync(1, "nginx", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.Operation == OperationKind.ServiceInstall && t.Command == "nginx"),
            Arg.Any<CancellationToken>());
        await _taskService.Received(1).NotifyTaskQueuedAsync(
            Arg.Is<ServerTask>(t => t.Operation == OperationKind.ServiceInstall && t.Command == "nginx"),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ServiceInstall", "Service", 1, "nginx", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallService_WellKnown_QueuesTypedOperationAndAudits()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Online, PackageManagementAvailable = true });

        await _sut.UninstallServiceAsync(1, "nginx", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.Operation == OperationKind.ServiceUninstall && t.Command == "nginx"),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ServiceUninstall", "Service", 1, "nginx", Arg.Any<CancellationToken>());
    }

    // S-FEAT-W8KN: the UI service key (docker) is resolved to the real apt package name (docker.io)
    // before the typed op is queued, so apt can actually locate it.
    [Fact]
    public async Task InstallService_ServiceKey_ResolvesToAptPackageName()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Online, PackageManagementAvailable = true });

        await _sut.InstallServiceAsync(1, "docker", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.Operation == OperationKind.ServiceInstall && t.Command == "docker.io"),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ServiceInstall", "Service", 1, "docker.io", Arg.Any<CancellationToken>());
    }

    // Protected packages (databases, ufw/fail2ban, docker) are installable but cannot be uninstalled -
    // refused before the task is ever queued.
    [Theory]
    [InlineData("postgresql")]
    [InlineData("docker")]
    [InlineData("ufw")]
    public async Task UninstallService_ProtectedPackage_ThrowsBadRequest_AndQueuesNothing(string service)
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Online, PackageManagementAvailable = true });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UninstallServiceAsync(1, service, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    // --- CreateServiceLogsTaskAsync ---

    [Fact]
    public async Task CreateServiceLogs_InvalidName_ThrowsBadRequest()
        => await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateServiceLogsTaskAsync(1, "bad;name", 100, false, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task CreateServiceLogs_Valid_QueuesTypedGetLogsOpAndAudits()
    {
        await _sut.CreateServiceLogsTaskAsync(1, "nginx", 50, follow: true, ct: TestContext.Current.CancellationToken);

        // Typed op (no shell interpolation): unit in the target, line count / follow in env, timeout 60 (follow).
        await _repo.Received(1).AddTaskAsync(
            Arg.Is<ServerTask>(t => t.ServerId == 1
                && t.Executor == Aetheus.Shared.Components.Tasks.ExecutorType.Operation
                && t.Operation == Aetheus.Shared.Components.Tasks.OperationKind.ServiceGetLogs
                && t.Command == "nginx"
                && t.TimeoutSeconds == 60
                && t.EnvironmentVariables.Contains("50")
                && t.EnvironmentVariables.Contains("true")),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("ServiceLogs", "Service", 1, "nginx", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, "1", "SystemUpgradeDryRun")]
    [InlineData(false, "0", "SystemUpgrade")]
    public async Task UpgradeSystem_CapableOnlineServer_QueuesTypedOperation(bool dryRun, string expectedFlag, string auditAction)
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server
        {
            Id = 1,
            Status = ServerStatus.Online,
            PatchManagementAvailable = true
        });

        await _sut.UpgradeSystemAsync(1, dryRun, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.SystemPackageUpgrade && task.Command == "-"
            && task.TimeoutSeconds == 900 && task.EnvironmentVariables.Contains(expectedFlag, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(auditAction, "Server", 1, null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ServerStatus.Offline, true)]
    [InlineData(ServerStatus.Online, false)]
    public async Task UpgradeSystem_OfflineOrMissingCapability_RejectsBeforeQueue(ServerStatus status, bool capability)
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server
        {
            Id = 1,
            Status = status,
            PatchManagementAvailable = capability
        });

        var error = await Record.ExceptionAsync(() => _sut.UpgradeSystemAsync(1, false, ct: TestContext.Current.CancellationToken));
        if (status == ServerStatus.Offline)
            Assert.IsType<ConflictException>(error);
        else
            Assert.IsType<BadRequestException>(error);
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    // --- Recette R3-59: read-only installed package list ---

    [Fact]
    public async Task ListInstalledPackages_CapableLinuxAgent_QueuesTheReadOnlyOperation_WithoutThePackageGrant()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server
        {
            Id = 1,
            Status = ServerStatus.Online,
            OsType = OsType.Linux,
            PackageManagementAvailable = false,
            AgentCapabilitiesJson = $"[\"{AgentCapabilities.PackageInventory}\",\"shell.execute\"]"
        });

        await _sut.ListInstalledPackagesAsync(1, TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(task =>
            task.ServerId == 1 && task.Operation == OperationKind.PackageInventory && task.Command == "-"
            && task.Name == InstalledPackages.TaskName && task.TimeoutSeconds == 120),
            Arg.Any<CancellationToken>());
        await _taskService.Received(1).NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ServerStatus.Offline, OsType.Linux, true, typeof(ConflictException))]
    [InlineData(ServerStatus.Online, OsType.Windows, true, typeof(BadRequestException))]
    [InlineData(ServerStatus.Online, OsType.Linux, false, typeof(BadRequestException))]
    public async Task ListInstalledPackages_OfflineWindowsOrOldAgent_RejectsBeforeQueue(
        ServerStatus status, OsType os, bool capability, Type expected)
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server
        {
            Id = 1,
            Name = "srv",
            Status = status,
            OsType = os,
            AgentCapabilitiesJson = capability ? $"[\"{AgentCapabilities.PackageInventory}\"]" : "[\"shell.execute\"]"
        });

        var error = await Record.ExceptionAsync(() => _sut.ListInstalledPackagesAsync(1, TestContext.Current.CancellationToken));

        Assert.IsType(expected, error);
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListInstalledPackages_UnknownServer_ThrowsNotFound()
    {
        _repo.FindServerAsync(99, Arg.Any<CancellationToken>()).Returns((Server?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.ListInstalledPackagesAsync(99, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSecurityUpdates_ProbeState_MapsHonestKnownFlagAndPackageList()
    {
        var checkedAt = new DateTime(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, PatchManagementAvailable = true });
        _heartbeatRepoMock.GetSecurityUpdatesStateAsync(1, Arg.Any<CancellationToken>()).Returns(new SecurityUpdatesState
        {
            ServerId = 1,
            Probed = true,
            PendingTotal = 2,
            PendingSecurity = 1,
            CheckedAt = checkedAt,
            UpdatesJson = "[{\"Package\":\"openssl\",\"CurrentVersion\":\"1\",\"CandidateVersion\":\"2\",\"IsSecurity\":true}]"
        });

        var result = await _sut.GetSecurityUpdatesAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Known);
        Assert.True(result.PackageManagerPresent);
        Assert.True(result.PatchManagementAvailable);
        Assert.Equal(2, result.PendingTotal);
        Assert.Equal("openssl", Assert.Single(result.Updates).Package);
        Assert.Equal(checkedAt, result.CheckedAt);
    }

    [Theory]
    [InlineData("icmp", "any", 443, "protocol")]
    [InlineData("tcp", "bad source!", 443, "source")]
    [InlineData("tcp", "any", 22, "administration port")]
    public async Task FirewallDeny_InvalidOrLockoutRule_RejectsBeforeServerLookup(
        string protocol, string source, int port, string expected)
    {
        var error = await Assert.ThrowsAsync<BadRequestException>(() => _sut.FirewallDenyAsync(1,
            new FirewallRuleRequest { Protocol = protocol, Source = source, Port = port }, ct: TestContext.Current.CancellationToken));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        await _repo.DidNotReceive().FindServerAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(OperationKind.FirewallAllow, "allow")]
    [InlineData(OperationKind.FirewallDeny, "deny")]
    [InlineData(OperationKind.FirewallDeleteRule, "delete")]
    public async Task FirewallRule_CapableServer_QueuesTypedOperationWithSanitizedEnvironment(
        OperationKind operation, string verb)
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server
        {
            Id = 1,
            Status = ServerStatus.Online,
            FirewallManagementAvailable = true
        });
        var request = new FirewallRuleRequest { Port = 8443, Protocol = "TCP", Source = "10.0.0.0/8" };

        if (operation == OperationKind.FirewallAllow)
            await _sut.FirewallAllowAsync(1, request, ct: TestContext.Current.CancellationToken);
        else if (operation == OperationKind.FirewallDeny)
            await _sut.FirewallDenyAsync(1, request, ct: TestContext.Current.CancellationToken);
        else
            await _sut.FirewallDeleteRuleAsync(1, request, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(task =>
            task.Operation == operation && task.Command == "8443"
            && task.Name.Contains(verb, StringComparison.OrdinalIgnoreCase)
            && task.EnvironmentVariables.Contains("tcp", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains("10.0.0.0/8", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, "enable", "FirewallEnable")]
    [InlineData(false, "disable", "FirewallDisable")]
    public async Task FirewallToggle_CapableServer_QueuesTypedOperation(bool enabled, string target, string auditAction)
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server
        {
            Id = 1,
            Status = ServerStatus.Online,
            FirewallManagementAvailable = true
        });

        await _sut.FirewallToggleAsync(1, enabled, ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.FirewallSetEnabled && task.Command == target), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(auditAction, "Firewall", 1, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFirewall_StateJson_MapsRulesAndCapability()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, FirewallManagementAvailable = true });
        _heartbeatRepoMock.GetFirewallStateAsync(1, Arg.Any<CancellationToken>()).Returns(new FirewallState
        {
            ServerId = 1,
            Installed = true,
            Active = true,
            StatusKnown = true,
            CollectionDiagnostics = string.Empty,
            RulesJson = "[{\"Number\":1,\"Action\":\"allow\",\"Port\":443,\"Protocol\":\"tcp\",\"Source\":\"any\"}]"
        });

        var result = await _sut.GetFirewallAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed);
        Assert.True(result.Active);
        Assert.True(result.StatusKnown);
        Assert.Empty(result.CollectionDiagnostics);
        Assert.True(result.FirewallManagementAvailable);
        Assert.Equal(443, Assert.Single(result.Rules).Port);
    }

    // --- Recette R3-24/R3-25/R3-43: a mail stack Aetheus manages is installed and removed whole ---

    [Theory]
    [InlineData("postfix")]
    [InlineData("dovecot")]
    public async Task UninstallingPartOfAManagedMailStack_IsRefused_AndQueuesNothing(string service)
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Online, PackageManagementAvailable = true });
        _repo.IsMailStackManagedAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => _sut.UninstallServiceAsync(1, service, ct: TestContext.Current.CancellationToken));

        Assert.Contains("Mail page", refusal.Message, StringComparison.Ordinal);
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallingDovecotOnAServerWithoutAManagedMailStack_StillQueues()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Status = ServerStatus.Online, PackageManagementAvailable = true });
        _repo.IsMailStackManagedAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await _sut.InstallServiceAsync(1, "dovecot", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }
}
