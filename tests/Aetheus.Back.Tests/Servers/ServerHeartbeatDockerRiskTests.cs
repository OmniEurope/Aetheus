// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>PLAN-010 lot 1: a container with host-escape settings raises a Sec-Audit alert when first seen
/// and when its settings change; the same state stays quiet across heartbeats and backend restarts; the
/// settings an Aetheus role needs are not alerted, anything beyond them is.</summary>
public sealed class ServerHeartbeatDockerRiskTests
{
    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly IHubClients _alertClients = Substitute.For<IHubClients>();
    private readonly IClientProxy _alertProxy = Substitute.For<IClientProxy>();
    private readonly Server _server = new() { Id = 7, Name = "web-1", Hostname = "web-1" };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly IDomainEventDispatcher _events = Substitute.For<IDomainEventDispatcher>();
    private int _beat;

    public ServerHeartbeatDockerRiskTests()
    {
        _repo.FindServerAsync(_server.Id, Arg.Any<CancellationToken>()).Returns(_server);
        _alertClients.Group(HubGroups.Alerts).Returns(_alertProxy);
    }

    // A fresh service per heartbeat: only the server row carries state, exactly as across restarts.
    private ServerService CreateService()
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(_alertClients);
        var confirmation = Substitute.For<IAgentUpdateConfirmationService>();
        confirmation.ProcessHeartbeatAsync(_server.Id, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        return new ServerService(_repo, Substitute.For<IServerHeartbeatRepository>(), hub, alertHub,
            Substitute.For<IAuditService>(), Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time,
            Substitute.For<IDbTransactionScope>(), updateConfirmation: confirmation, domainEvents: _events);
    }

    private static DockerContainerDto Container(
        string name,
        DockerContainerRisks risks = DockerContainerRisks.None,
        string caps = "",
        AetheusContainerRole role = AetheusContainerRole.None,
        bool inspected = true) => new()
        {
            ContainerId = name.Length >= 12 ? name[..12] : name.PadRight(12, '0'),
            Name = name,
            Image = "img:1",
            State = "running",
            SecurityInspected = inspected,
            Risks = risks,
            DangerousCapabilities = caps,
            AetheusRole = role
        };

    // Each beat changes the CPU reading, so the container fingerprint always changes, as in production.
    private Task HeartbeatAsync(params DockerContainerDto[] containers)
    {
        _beat++;
        return CreateService().ProcessHeartbeatAsync(_server.Id, new ServerHeartbeatDto
        {
            AgentVersion = "1.0.0",
            Docker = new DockerDataDto { Containers = containers.Select(c => c with { CpuPercent = _beat }).ToList() }
        }, TestContext.Current.CancellationToken);
    }

    private List<AlertTriggeredDto> RiskAlerts() => _alertProxy.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))
        .Select(call => call.GetArguments())
        .Where(arguments => Equals(arguments[0], "AlertTriggered"))
        .Select(arguments => ((object?[])arguments[1]!)[0])
        .OfType<AlertTriggeredDto>()
        .Where(alert => alert.Metric == "DockerContainerRisk")
        .ToList();

    private List<DockerContainerRiskDetectedEvent> RiskEvents() => _events.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IDomainEventDispatcher.DispatchAsync))
        .Select(call => call.GetArguments()[0])
        .OfType<DockerContainerRiskDetectedEvent>()
        .ToList();

    [Fact]
    public async Task PrivilegedContainer_AlertsOnce_ThenStaysQuietAcrossHeartbeatsAndRestarts()
    {
        await HeartbeatAsync(Container("web"), Container("probe", DockerContainerRisks.Privileged));
        await HeartbeatAsync(Container("web"), Container("probe", DockerContainerRisks.Privileged));
        await HeartbeatAsync(Container("web"), Container("probe", DockerContainerRisks.Privileged));

        var alert = Assert.Single(RiskAlerts());
        Assert.Equal("Sec-Audit", alert.RuleName);
        Assert.Equal("Critical", alert.Severity);
        Assert.Equal(_server.Id, alert.ServerId);
        Assert.Contains("probe", alert.Message, StringComparison.Ordinal);
        Assert.Contains("privileged", alert.Message, StringComparison.Ordinal);
        var raised = Assert.Single(RiskEvents());
        Assert.False(raised.IsChange);
        Assert.Equal("probe", raised.ContainerName);
        Assert.Contains("\"probe\"", _server.DockerRiskAlertedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"web\"", _server.DockerRiskAlertedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedSettings_AlertAgain_AsAChange()
    {
        await HeartbeatAsync(Container("probe", DockerContainerRisks.HostNetwork));
        await HeartbeatAsync(Container("probe", DockerContainerRisks.HostNetwork | DockerContainerRisks.DockerSocket));

        var alerts = RiskAlerts();
        Assert.Equal(2, alerts.Count);
        Assert.Equal("Warning", alerts[0].Severity);
        Assert.Equal("Critical", alerts[1].Severity);
        Assert.Contains("Docker socket mounted", alerts[1].Message, StringComparison.Ordinal);
        Assert.True(RiskEvents()[1].IsChange);
    }

    [Fact]
    public async Task RemovedThenRecreated_IsANewObservation()
    {
        await HeartbeatAsync(Container("web"), Container("probe", DockerContainerRisks.HostPid));
        await HeartbeatAsync(Container("web"));
        Assert.Null(_server.DockerRiskAlertedJson);
        await HeartbeatAsync(Container("web"), Container("probe", DockerContainerRisks.HostPid));

        Assert.Equal(2, RiskAlerts().Count);
    }

    [Fact]
    public async Task AetheusBuildKitBuilder_PrivilegedIsExpected_ButASocketMountOnItIsAlerted()
    {
        await HeartbeatAsync(Container("buildx_buildkit_aetheus-a0", DockerContainerRisks.Privileged,
            role: AetheusContainerRole.BuildKitBuilder));
        Assert.Empty(RiskAlerts());

        await HeartbeatAsync(Container("buildx_buildkit_aetheus-a0",
            DockerContainerRisks.Privileged | DockerContainerRisks.DockerSocket, role: AetheusContainerRole.BuildKitBuilder));

        var alert = Assert.Single(RiskAlerts());
        Assert.Contains("Docker socket mounted", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("privileged", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DangerousCapabilities_SysAdminIsCritical_NetAdminAloneIsAWarning()
    {
        await HeartbeatAsync(
            Container("a", DockerContainerRisks.DangerousCapabilities, "NET_ADMIN"),
            Container("b", DockerContainerRisks.DangerousCapabilities, "SYS_ADMIN"));

        var alerts = RiskAlerts().OrderBy(a => a.Message, StringComparer.Ordinal).ToList();
        Assert.Equal("Warning", alerts[0].Severity);
        Assert.Contains("capabilities NET_ADMIN", alerts[0].Message, StringComparison.Ordinal);
        Assert.Equal("Critical", alerts[1].Severity);
    }

    [Fact]
    public async Task UninspectedContainers_AreUnknown_TheyNeitherAlertNorClearTheAlertedState()
    {
        await HeartbeatAsync(Container("probe", DockerContainerRisks.Privileged));
        var alerted = _server.DockerRiskAlertedJson;

        // An older agent, or an inspect that failed: nothing is inspected, nothing changes.
        await HeartbeatAsync(Container("probe", inspected: false));
        Assert.Equal(alerted, _server.DockerRiskAlertedJson);

        // One container inspected, the flagged one not: its state is kept, so it does not alert again later.
        await HeartbeatAsync(Container("web"), Container("probe", inspected: false));
        await HeartbeatAsync(Container("web"), Container("probe", DockerContainerRisks.Privileged));

        Assert.Single(RiskAlerts());
    }
}
