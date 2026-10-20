// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Components.Servers.Handlers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>PLAN-010 lot 1: the SSH snapshot goes through the real heartbeat path; the first one
/// is the baseline, a change is stored and alerted, an unreadable file is never taken as safe.</summary>
public sealed class ServerHeartbeatSshConfigTests
{
    private const string RootKeys = "/root/.ssh/authorized_keys";
    private const string AliceKeys = "/home/alice/.ssh/authorized_keys";
    private const string KeyA = "SHA256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyB = "SHA256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly IHubClients _alertClients = Substitute.For<IHubClients>();
    private readonly IClientProxy _alertProxy = Substitute.For<IClientProxy>();
    private readonly IAgentUpdateConfirmationService _confirmation = Substitute.For<IAgentUpdateConfirmationService>();
    private readonly Server _server = new() { Id = 7, Name = "web-1", Hostname = "web-1" };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly IDomainEventDispatcher _events = Substitute.For<IDomainEventDispatcher>();

    public ServerHeartbeatSshConfigTests()
    {
        _repo.FindServerAsync(_server.Id, Arg.Any<CancellationToken>()).Returns(_server);
        _alertClients.Group(HubGroups.Alerts).Returns(_alertProxy);
        _confirmation.ProcessHeartbeatAsync(_server.Id, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
    }

    private ServerService CreateService()
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(_alertClients);
        return new ServerService(_repo, Substitute.For<IServerHeartbeatRepository>(), hub, alertHub,
            Substitute.For<IAuditService>(), Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time,
            Substitute.For<IDbTransactionScope>(), updateConfirmation: _confirmation, domainEvents: _events);
    }

    private static SshConfigDataDto Snapshot(
        string? permitRootLogin = "no",
        AuthorizedKeysFileState rootState = AuthorizedKeysFileState.Unreadable,
        params string[] aliceKeys) => new()
        {
            Collected = true,
            PermitRootLogin = new SshSettingDto { Value = permitRootLogin, Source = permitRootLogin is null ? null : "/etc/ssh/sshd_config:3" },
            PasswordAuthentication = new SshSettingDto { Value = "no", Source = "/etc/ssh/sshd_config:4" },
            AuthorizedKeysFile = new SshSettingDto { Value = ".ssh/authorized_keys", Source = "default" },
            AuthorizedKeys =
        [
            new AuthorizedKeysFileDto { User = "root", Path = RootKeys, State = rootState },
            new AuthorizedKeysFileDto
            {
                User = "alice",
                Path = AliceKeys,
                State = AuthorizedKeysFileState.Read,
                Keys = [.. aliceKeys.Select(k => new AuthorizedKeyDto { KeyType = "ssh-ed25519", Fingerprint = k })]
            }
        ]
        };

    private Task HeartbeatAsync(SshConfigDataDto? ssh) =>
        CreateService().ProcessHeartbeatAsync(_server.Id, new ServerHeartbeatDto
        {
            AgentVersion = "1.0.0",
            SshConfig = ssh
        }, TestContext.Current.CancellationToken);

    private List<AlertTriggeredDto> SshAlerts() => _alertProxy.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))
        .Select(call => call.GetArguments())
        .Where(arguments => Equals(arguments[0], "AlertTriggered"))
        .Select(arguments => ((object?[])arguments[1]!)[0])
        .OfType<AlertTriggeredDto>()
        .Where(alert => alert.Metric == SshConfigMonitor.Metric)
        .ToList();

    private List<SshConfigChangedEvent> SshEvents() => _events.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IDomainEventDispatcher.DispatchAsync))
        .Select(call => call.GetArguments()[0])
        .OfType<SshConfigChangedEvent>()
        .ToList();

    private SshConfigStatusDto Stored() => SshConfigMonitor.Read(_server.SshConfigStateJson)!;

    [Fact]
    public async Task FirstSnapshot_IsTheBaseline_WithoutAlert()
    {
        await HeartbeatAsync(Snapshot(aliceKeys: KeyA));

        var stored = Stored();
        Assert.Equal("no", stored.Snapshot.PermitRootLogin.Value);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, stored.BaselineAt);
        Assert.Empty(stored.RecentChanges);
        Assert.Empty(SshAlerts());
        Assert.Empty(SshEvents());
    }

    [Fact]
    public async Task SameSnapshot_ChangesNothing()
    {
        await HeartbeatAsync(Snapshot(aliceKeys: KeyA));
        var before = _server.SshConfigStateJson;
        _time.Advance(TimeSpan.FromMinutes(5));

        await HeartbeatAsync(Snapshot(aliceKeys: KeyA));

        Assert.Equal(before, _server.SshConfigStateJson);
        Assert.Empty(SshAlerts());
    }

    [Fact]
    public async Task PermitRootLoginToYes_IsACriticalAlertAndEvent()
    {
        await HeartbeatAsync(Snapshot());
        _time.Advance(TimeSpan.FromMinutes(5));

        await HeartbeatAsync(Snapshot(permitRootLogin: "yes"));

        var alert = Assert.Single(SshAlerts());
        Assert.Equal("Critical", alert.Severity);
        Assert.Equal("PermitRootLogin changed to yes (was no)", alert.Message);
        var change = Assert.Single(Stored().RecentChanges);
        Assert.Equal("PermitRootLogin changed to yes (was no)", change.Message);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, change.DetectedAt);
        Assert.Equal("Critical", Assert.Single(SshEvents()).Severity);
    }

    [Fact]
    public async Task KeyAdded_IsCritical_KeyRemoved_IsAWarning()
    {
        await HeartbeatAsync(Snapshot(aliceKeys: KeyA));

        await HeartbeatAsync(Snapshot(aliceKeys: [KeyA, KeyB]));
        await HeartbeatAsync(Snapshot(aliceKeys: KeyB));

        var alerts = SshAlerts();
        Assert.Equal(2, alerts.Count);
        Assert.Equal($"new key added to {AliceKeys} (ssh-ed25519 {KeyB})", alerts[0].Message);
        Assert.Equal("Critical", alerts[0].Severity);
        Assert.Equal($"key removed from {AliceKeys} (ssh-ed25519 {KeyA})", alerts[1].Message);
        Assert.Equal("Warning", alerts[1].Severity);
        // Most recent first.
        Assert.Equal(alerts[1].Message, Stored().RecentChanges[0].Message);
    }

    [Fact]
    public async Task RootKeysBecomingReadable_BecomeTheReference_WithoutAlert_ThenANewKeyAlerts()
    {
        await HeartbeatAsync(Snapshot(rootState: AuthorizedKeysFileState.Unreadable));
        var readable = Snapshot(rootState: AuthorizedKeysFileState.Read);
        readable.AuthorizedKeys[0] = readable.AuthorizedKeys[0] with { Keys = [new AuthorizedKeyDto { KeyType = "ssh-rsa", Fingerprint = KeyA }] };

        await HeartbeatAsync(readable);
        Assert.Empty(SshAlerts());
        Assert.Equal("Info", Stored().RecentChanges[0].Severity);

        var added = readable with { AuthorizedKeys = [.. readable.AuthorizedKeys] };
        added.AuthorizedKeys[0] = added.AuthorizedKeys[0] with
        {
            Keys = [.. added.AuthorizedKeys[0].Keys, new AuthorizedKeyDto { KeyType = "ssh-ed25519", Fingerprint = KeyB }]
        };
        await HeartbeatAsync(added);

        Assert.Equal($"new key added to {RootKeys} (ssh-ed25519 {KeyB})", Assert.Single(SshAlerts()).Message);
    }

    [Fact]
    public async Task ValueBecomingUnknown_IsAWarning_NeverSilent()
    {
        await HeartbeatAsync(Snapshot(permitRootLogin: "no"));

        await HeartbeatAsync(Snapshot(permitRootLogin: null));

        var alert = Assert.Single(SshAlerts());
        Assert.Equal("Warning", alert.Severity);
        Assert.StartsWith("PermitRootLogin is now unknown", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyOrNotCollected_LeavesTheStoredSnapshot()
    {
        await HeartbeatAsync(Snapshot());
        var before = _server.SshConfigStateJson;

        await HeartbeatAsync(null);
        await HeartbeatAsync(new SshConfigDataDto { Collected = false });

        Assert.Equal(before, _server.SshConfigStateJson);
        Assert.Empty(SshAlerts());
    }

    [Fact]
    public async Task RecentChanges_AreBounded()
    {
        await HeartbeatAsync(Snapshot(permitRootLogin: "no"));
        for (var i = 0; i < SshConfigLimits.MaxStoredChanges + 5; i++)
            await HeartbeatAsync(Snapshot(permitRootLogin: i % 2 == 0 ? "yes" : "no"));

        Assert.Equal(SshConfigLimits.MaxStoredChanges, Stored().RecentChanges.Count);
    }

    [Fact]
    public async Task NotificationHandler_RecordsAnAdministratorEventAndPublishes()
    {
        var users = Substitute.For<IUserNotificationService>();
        var channels = Substitute.For<INotificationService>();
        var handler = new SshConfigChangeNotificationHandler(users, channels);

        await handler.HandleAsync(
            new SshConfigChangedEvent(7, "web-1", "Critical", "PermitRootLogin changed to yes (was no)", _time.GetUtcNow().UtcDateTime),
            TestContext.Current.CancellationToken);

        await users.Received(1).RecordAdministratorEventAsync(
            "alert.triggered",
            "Sec-Audit: SSH configuration changed on server web-1",
            Arg.Is<string>(payload => payload.Contains("PermitRootLogin changed to yes", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await channels.Received(1).SendEventAsync("alert.triggered", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }
}
