// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
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

/// <summary>PLAN-010 lot 1: a heartbeat reporting new OOM kills raises one alert naming the killed
/// processes; an unknown or first report never does.</summary>
public sealed class ServerHeartbeatOomKillTests
{
    private const string Boot1 = "11111111-1111-1111-1111-111111111111";
    private const string Boot2 = "22222222-2222-2222-2222-222222222222";

    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly IHubClients _alertClients = Substitute.For<IHubClients>();
    private readonly IClientProxy _alertProxy = Substitute.For<IClientProxy>();
    private readonly Server _server = new() { Id = 9, Name = "vps-1", Hostname = "vps-1" };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly IDomainEventDispatcher _events = Substitute.For<IDomainEventDispatcher>();

    public ServerHeartbeatOomKillTests()
    {
        _repo.FindServerAsync(_server.Id, Arg.Any<CancellationToken>()).Returns(_server);
        _alertClients.Group(HubGroups.Alerts).Returns(_alertProxy);
    }

    // A fresh service per heartbeat: only the server row carries state, as across backend restarts.
    private Task HeartbeatAsync(OomKillReportDto? report, params (string Id, string Name)[] containers)
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(_alertClients);
        var service = new ServerService(_repo, Substitute.For<IServerHeartbeatRepository>(), hub, alertHub,
            Substitute.For<IAuditService>(), Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time,
            Substitute.For<IDbTransactionScope>(), domainEvents: _events);
        return service.ProcessHeartbeatAsync(_server.Id, new ServerHeartbeatDto
        {
            AgentVersion = "1.0.0",
            OomKills = report,
            Docker = new DockerDataDto { Containers = [.. containers.Select(c => new DockerContainerDto { ContainerId = c.Id, Name = c.Name })] }
        }, TestContext.Current.CancellationToken);
    }

    private static OomKillReportDto Report(string bootId, long count, bool readable = true, params (int Pid, string Process)[] victims) => new()
    {
        BootId = bootId,
        KillCount = count,
        VictimsReadable = readable,
        RecentVictims = [.. victims.Select(v => new OomKillVictimDto { Pid = v.Pid, Process = v.Process })]
    };

    private List<AlertTriggeredDto> OomAlerts() => _alertProxy.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))
        .Select(call => call.GetArguments())
        .Where(arguments => Equals(arguments[0], "AlertTriggered"))
        .Select(arguments => ((object?[])arguments[1]!)[0])
        .OfType<AlertTriggeredDto>()
        .Where(alert => alert.Metric == "OomKill")
        .ToList();

    private List<OomKillDetectedEvent> OomEvents() => _events.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IDomainEventDispatcher.DispatchAsync))
        .Select(call => call.GetArguments()[0])
        .OfType<OomKillDetectedEvent>()
        .ToList();

    [Fact]
    public async Task FirstReport_RecordsTheBaselineWithoutAlert()
    {
        await HeartbeatAsync(Report(Boot1, 5, true, (10, "old")));

        Assert.Equal(Boot1, _server.OomKillBootId);
        Assert.Equal(5, _server.OomKillCount);
        Assert.Empty(OomAlerts());
    }

    [Fact]
    public async Task NewKill_RaisesOneCriticalAlertNamingTheProcessAndPid()
    {
        await HeartbeatAsync(Report(Boot1, 2, true, (10, "old")));
        await HeartbeatAsync(Report(Boot1, 3, true, (10, "old"), (4242, "stress")));

        var alert = Assert.Single(OomAlerts());
        Assert.Equal("OOM kill on vps-1: stress (pid 4242).", alert.Message);
        Assert.Equal("Critical", alert.Severity);
        Assert.Equal(_server.Id, alert.ServerId);
        var domainEvent = Assert.Single(OomEvents());
        Assert.Equal(1, domainEvent.KillCount);
        Assert.Equal(["stress (pid 4242)"], domainEvent.Victims);
        Assert.Equal(3, _server.OomKillCount);
    }

    [Fact]
    public async Task SameCounter_DoesNotAlertAgain()
    {
        await HeartbeatAsync(Report(Boot1, 0));
        await HeartbeatAsync(Report(Boot1, 1, true, (4242, "stress")));
        await HeartbeatAsync(Report(Boot1, 1, true, (4242, "stress")));
        await HeartbeatAsync(Report(Boot1, 1, true, (4242, "stress")));

        Assert.Single(OomAlerts());
    }

    [Fact]
    public async Task SeveralKills_NameTheNewestOnesOnly()
    {
        await HeartbeatAsync(Report(Boot1, 1, true, (10, "old")));
        await HeartbeatAsync(Report(Boot1, 3, true, (10, "old"), (20, "php-fpm"), (30, "java")));

        var alert = Assert.Single(OomAlerts());
        Assert.Equal("OOM kill on vps-1: php-fpm (pid 20), java (pid 30).", alert.Message);
    }

    [Fact]
    public async Task KernelLogNotReadable_ReportsTheCountWithoutAName()
    {
        await HeartbeatAsync(Report(Boot1, 0, readable: false));
        await HeartbeatAsync(Report(Boot1, 2, readable: false));

        var alert = Assert.Single(OomAlerts());
        Assert.Equal("2 OOM kills on vps-1; processes not named (the agent cannot read the kernel log).", alert.Message);
        Assert.Empty(Assert.Single(OomEvents()).Victims);
    }

    [Fact]
    public async Task MoreKillsThanNamedVictims_SaysHowManyAreNotNamed()
    {
        await HeartbeatAsync(Report(Boot1, 0));
        await HeartbeatAsync(Report(Boot1, 3, true, (4242, "stress")));

        Assert.Equal("OOM kill on vps-1: stress (pid 4242) and 2 more not named.", Assert.Single(OomAlerts()).Message);
    }

    [Fact]
    public async Task Reboot_CountsEveryKillOfTheNewBoot()
    {
        await HeartbeatAsync(Report(Boot1, 7));
        await HeartbeatAsync(Report(Boot2, 0));
        Assert.Empty(OomAlerts());

        await HeartbeatAsync(Report(Boot2, 1, true, (99, "node")));

        Assert.Equal("OOM kill on vps-1: node (pid 99).", Assert.Single(OomAlerts()).Message);
        Assert.Equal(Boot2, _server.OomKillBootId);
    }

    [Fact]
    public async Task RebootWithKillsBeforeTheFirstBeat_AlertsThem()
    {
        await HeartbeatAsync(Report(Boot1, 7));
        await HeartbeatAsync(Report(Boot2, 1, true, (99, "node")));

        Assert.Equal("OOM kill on vps-1: node (pid 99).", Assert.Single(OomAlerts()).Message);
    }

    [Fact]
    public async Task UnknownReport_ClearsTheBaselineAndNeverAlerts()
    {
        await HeartbeatAsync(Report(Boot1, 1));
        await HeartbeatAsync(null);

        Assert.Null(_server.OomKillCount);
        Assert.Null(_server.OomKillBootId);

        // Known again: a new baseline, not a kill.
        await HeartbeatAsync(Report(Boot1, 4, true, (5, "x")));
        Assert.Empty(OomAlerts());
        Assert.Empty(OomEvents());
    }

    // Figures of the 2026-10-24 production incident as the agent reports them: dotnet pid 827947,
    // 663552 kB anonymous, 43008 kB file, 208896 kB shared; the container id is invented (see the
    // agent tests for the reconstructed kernel lines).
    private static OomKillReportDto CgroupKillReport(long count, string? containerId = "a1b2c3d4e5f6") => new()
    {
        BootId = Boot1,
        KillCount = count,
        VictimsReadable = true,
        RecentVictims =
        [
            new OomKillVictimDto
            {
                Pid = 827947,
                Process = "dotnet",
                Kind = OomKillKind.Cgroup,
                Cgroup = "/system.slice/docker-a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90.scope",
                ContainerId = containerId,
                AnonRssKb = 663552,
                FileRssKb = 43008,
                ShmemRssKb = 208896
            }
        ]
    };

    [Fact]
    public async Task ContainerLimitKill_SaysTheContainerLimitWasReachedWithItsNameAndTheVictimMemory()
    {
        await HeartbeatAsync(Report(Boot1, 0));
        await HeartbeatAsync(CgroupKillReport(1), ("ffffffffffff", "other"), ("a1b2c3d4e5f6", "aetheus-back-green"));

        var alert = Assert.Single(OomAlerts());
        Assert.Equal(
            "OOM kill on vps-1: container memory limit reached (aetheus-back-green): dotnet (pid 827947), 894 MB resident (anon 648 MB, file 42 MB, shmem 204 MB).",
            alert.Message);
        var kill = Assert.Single(Assert.Single(OomEvents()).Kills!);
        Assert.Equal(OomKillKind.Cgroup, kill.Kind);
        Assert.Equal("aetheus-back-green", kill.Container);
        Assert.Equal("dotnet", kill.Process);
        Assert.Equal(827947, kill.Pid);
        Assert.Equal(663552, kill.AnonRssKb);
        Assert.Equal(["dotnet (pid 827947)"], Assert.Single(OomEvents()).Victims);
    }

    [Fact]
    public async Task ContainerLimitKill_WithAContainerTheHeartbeatDoesNotList_ShowsItsShortId()
    {
        await HeartbeatAsync(Report(Boot1, 0));
        await HeartbeatAsync(CgroupKillReport(1));

        Assert.StartsWith(
            "OOM kill on vps-1: container memory limit reached (a1b2c3d4e5f6): dotnet (pid 827947), 894 MB resident",
            Assert.Single(OomAlerts()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CgroupKillOutsideDocker_NamesTheCgroupPath()
    {
        await HeartbeatAsync(Report(Boot1, 0));
        await HeartbeatAsync(new OomKillReportDto
        {
            BootId = Boot1,
            KillCount = 1,
            VictimsReadable = true,
            RecentVictims = [new OomKillVictimDto { Pid = 5, Process = "php-fpm", Kind = OomKillKind.Cgroup, Cgroup = "/system.slice/php.service" }]
        });

        Assert.Equal(
            "OOM kill on vps-1: cgroup memory limit reached (/system.slice/php.service): php-fpm (pid 5).",
            Assert.Single(OomAlerts()).Message);
    }

    [Fact]
    public async Task GlobalKill_SaysTheServerRanOutOfMemory()
    {
        await HeartbeatAsync(Report(Boot1, 0));
        await HeartbeatAsync(new OomKillReportDto
        {
            BootId = Boot1,
            KillCount = 1,
            VictimsReadable = true,
            RecentVictims = [new OomKillVictimDto { Pid = 4242, Process = "stress", Kind = OomKillKind.Global, AnonRssKb = 1998336, FileRssKb = 1024, ShmemRssKb = 0 }]
        });

        Assert.Equal(
            "OOM kill on vps-1: server out of memory: stress (pid 4242), 1,953 MB resident (anon 1,952 MB, file 1 MB, shmem 0 MB).",
            Assert.Single(OomAlerts()).Message);
    }

    [Fact]
    public async Task OlderAgentWithoutKinds_ProducesTodaysMessageAndNoDetail()
    {
        await HeartbeatAsync(Report(Boot1, 0));
        // Exactly what an agent from before the kinds sends: pid and process only.
        await HeartbeatAsync(System.Text.Json.JsonSerializer.Deserialize<OomKillReportDto>(
            """{"bootId":"11111111-1111-1111-1111-111111111111","killCount":1,"victimsReadable":true,"recentVictims":[{"pid":4242,"process":"stress"}]}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));

        Assert.Equal("OOM kill on vps-1: stress (pid 4242).", Assert.Single(OomAlerts()).Message);
        Assert.Empty(Assert.Single(OomEvents()).Kills ?? []);
    }

    [Fact]
    public async Task NotificationHandler_CarriesTheKindTheContainerAndTheMemoryInThePayload()
    {
        var userNotifications = Substitute.For<IUserNotificationService>();
        var notifications = Substitute.For<INotificationService>();
        var occurrences = Substitute.For<IAlertOccurrenceService>();
        var handler = new OomKillNotificationHandler(userNotifications, notifications, occurrences);
        var domainEvent = new OomKillDetectedEvent(9, "vps-1", 1, ["dotnet (pid 827947)"], "message", _time.GetUtcNow().UtcDateTime,
            [new OomKillDetail("dotnet", 827947, OomKillKind.Cgroup, "aetheus-back-green", "/system.slice/x.scope", 663552, 43008, 208896)]);

        await handler.HandleAsync(domainEvent, TestContext.Current.CancellationToken);

        await userNotifications.Received(1).RecordAdministratorEventAsync(
            "alert.triggered", Arg.Any<string>(),
            Arg.Is<string>(payload => payload.Contains("\"Kind\":\"Cgroup\"") && payload.Contains("\"Container\":\"aetheus-back-green\"")
                && payload.Contains("\"AnonRssKb\":663552") && payload.Contains("\"Victims\":[\"dotnet (pid 827947)\"]")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotificationHandler_RecordsForAdministratorsAndPublishesAlertTriggered()
    {
        var userNotifications = Substitute.For<IUserNotificationService>();
        var notifications = Substitute.For<INotificationService>();
        var occurrences = Substitute.For<IAlertOccurrenceService>();
        occurrences.RecordAsync(Arg.Any<NewAlertOccurrence>(), Arg.Any<CancellationToken>()).Returns(42L);
        var handler = new OomKillNotificationHandler(userNotifications, notifications, occurrences);
        var domainEvent = new OomKillDetectedEvent(9, "vps-1", 1, ["stress (pid 4242)"],
            "OOM kill on vps-1: stress (pid 4242).", _time.GetUtcNow().UtcDateTime);

        await handler.HandleAsync(domainEvent, TestContext.Current.CancellationToken);

        await occurrences.Received(1).RecordAsync(
            Arg.Is<NewAlertOccurrence>(occurrence => occurrence.ServerId == 9
                && occurrence.Metric == "OomKill"
                && occurrence.Severity == AlertSeverity.Critical
                && occurrence.TriggeredAt == domainEvent.DetectedAt),
            Arg.Any<CancellationToken>());
        await notifications.Received(1).SendEventAsync(
            "alert.triggered",
            Arg.Is<object>(payload => System.Text.Json.JsonSerializer.Serialize(payload).Contains("\"AlertId\":42", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        await userNotifications.Received(1).RecordAdministratorEventAsync(
            "alert.triggered", "OOM kill on server vps-1",
            Arg.Is<string>(payload => payload.Contains("stress (pid 4242)") && payload.Contains("\"Metric\":\"OomKill\"")),
            Arg.Any<CancellationToken>());
        await notifications.Received(1).SendEventAsync("alert.triggered", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }
}
