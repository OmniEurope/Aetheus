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
    private Task HeartbeatAsync(OomKillReportDto? report)
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(_alertClients);
        var service = new ServerService(_repo, Substitute.For<IServerHeartbeatRepository>(), hub, alertHub,
            Substitute.For<IAuditService>(), Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time,
            Substitute.For<IDbTransactionScope>(), domainEvents: _events);
        return service.ProcessHeartbeatAsync(_server.Id, new ServerHeartbeatDto { AgentVersion = "1.0.0", OomKills = report },
            TestContext.Current.CancellationToken);
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
