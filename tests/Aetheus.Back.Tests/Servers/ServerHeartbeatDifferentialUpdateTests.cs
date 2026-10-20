// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Rewrite changed inventory sections, with a full rewrite on the first heartbeat and
/// after FullRewriteInterval.</summary>
public sealed class ServerHeartbeatDifferentialUpdateTests
{
    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly IServerHeartbeatRepository _heartbeatRepo = Substitute.For<IServerHeartbeatRepository>();
    private readonly IClientProxy _alertProxy = Substitute.For<IClientProxy>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly Server _server = new() { Id = 3, Name = "web-3", Hostname = "web-3" };

    public ServerHeartbeatDifferentialUpdateTests()
    {
        _repo.FindServerAsync(_server.Id, Arg.Any<CancellationToken>()).Returns(_server);
    }

    // A fresh service per beat: only the server row carries state between beats, as across restarts.
    private Task BeatAsync(ServerHeartbeatDto heartbeat)
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertClients = Substitute.For<IHubClients>();
        alertClients.Group(HubGroups.Alerts).Returns(_alertProxy);
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(alertClients);
        var service = new ServerService(_repo, _heartbeatRepo, hub, alertHub, Substitute.For<IAuditService>(),
            Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), _time, Substitute.For<IDbTransactionScope>());
        return service.ProcessHeartbeatAsync(_server.Id, heartbeat, TestContext.Current.CancellationToken);
    }

    private static ServerHeartbeatDto Inventory(string serviceStatus = "running", int blockedCount = 2) => new()
    {
        AgentVersion = "1.2.0",
        Services = [new ServiceInfoDto { Name = "nginx", Type = ServiceType.Systemd, Status = serviceStatus, IsRunning = true }],
        Docker = new DockerDataDto
        {
            Containers = [new DockerContainerDto { ContainerId = "c1", Name = "app", Image = "app:1", State = "running" }],
            Images = [new DockerImageDto { ImageId = "sha256:1", Repository = "app", Tag = "1" }],
            ComposeStacks = [new DockerComposeStackDto { Name = "app", Status = "running" }],
            Networks = [new DockerNetworkDto { NetworkId = "n1", Name = "bridge", Driver = "bridge" }],
            Volumes = [new DockerVolumeDto { Name = "data", Driver = "local" }]
        },
        Apache = new ApacheDataDto { IsInstalled = true, Version = "2.4" },
        Certbot = new CertbotDataDto { IsInstalled = true, RenewalCheckSucceeded = true },
        Mail = new MailDataDto { IsInstalled = true, PostfixVersion = "3.8" },
        Teamspeak = new TeamspeakDataDto { IsInstalled = true, Version = "3.13" },
        Portsentry = new PortsentryDataDto { IsInstalled = true, BlockedCount = blockedCount },
        Rkhunter = new RkhunterDataDto { IsInstalled = true, WarningCount = 1 },
        SecurityUpdates = new SecurityUpdatesDataDto { PackageManagerPresent = true, ProbeSucceeded = true, PendingTotal = 3 },
        Firewall = new FirewallDataDto { Installed = true, Active = true, StatusKnown = true }
    };

    /// <summary>Counts every call to the repository method <paramref name="name"/>.</summary>
    private int Calls(string name) =>
        _heartbeatRepo.ReceivedCalls().Count(call => call.GetMethodInfo().Name == name);

    private static readonly string[] SectionWrites =
    [
        nameof(IServerHeartbeatRepository.ReplaceServicesAsync),
        nameof(IServerHeartbeatRepository.ReplaceDockerContainersAsync),
        nameof(IServerHeartbeatRepository.ReplaceDockerImagesAsync),
        nameof(IServerHeartbeatRepository.ReplaceDockerComposeStacksAsync),
        nameof(IServerHeartbeatRepository.ReplaceDockerNetworksAsync),
        nameof(IServerHeartbeatRepository.ReplaceDockerVolumesAsync),
        nameof(IServerHeartbeatRepository.ReplaceApacheDataAsync),
        nameof(IServerHeartbeatRepository.ReplaceCertbotCertificatesAsync),
        nameof(IServerHeartbeatRepository.ReplaceCertbotStateAsync),
        nameof(IServerHeartbeatRepository.ReplaceMailDataAsync),
        nameof(IServerHeartbeatRepository.ReplaceTeamspeakDataAsync),
        nameof(IServerHeartbeatRepository.ReplacePortsentryDataAsync),
        nameof(IServerHeartbeatRepository.ReplaceRkhunterDataAsync),
        nameof(IServerHeartbeatRepository.ReplaceSecurityUpdatesDataAsync),
        nameof(IServerHeartbeatRepository.ReplaceFirewallDataAsync)
    ];

    private Dictionary<string, int> WriteCounts() => SectionWrites.ToDictionary(name => name, Calls);

    [Fact]
    public async Task FirstHeartbeat_WritesEverySectionAndStoresTheFingerprints()
    {
        await BeatAsync(Inventory());

        Assert.All(WriteCounts(), write => Assert.Equal(1, write.Value));
        Assert.Equal(1, Calls(nameof(IServerHeartbeatRepository.GetSecurityCountersAsync)));
        Assert.Equal(0, Calls(nameof(IServerHeartbeatRepository.TouchSecurityUpdatesCheckedAtAsync)));
        Assert.Equal(1, Calls(nameof(IServerHeartbeatRepository.AddMetricAsync)));
        Assert.NotNull(_server.HeartbeatInventoryFingerprintsJson);
    }

    [Fact]
    public async Task UnchangedInventory_DeletesAndInsertsNothing_ButStillRecordsTheMetric()
    {
        await BeatAsync(Inventory());
        var fingerprints = _server.HeartbeatInventoryFingerprintsJson;
        _time.Advance(TimeSpan.FromSeconds(30));

        await BeatAsync(Inventory());

        Assert.All(WriteCounts(), write => Assert.Equal(1, write.Value));
        Assert.Equal(1, Calls(nameof(IServerHeartbeatRepository.GetSecurityCountersAsync)));
        Assert.Equal(1, Calls(nameof(IServerHeartbeatRepository.GetCertbotRenewalCheckedAtAsync)));
        Assert.Equal(2, Calls(nameof(IServerHeartbeatRepository.AddMetricAsync)));
        // The unchanged security-updates report still records its newer check, as one UPDATE.
        await _heartbeatRepo.Received(1).TouchSecurityUpdatesCheckedAtAsync(
            _server.Id, _time.GetUtcNow().UtcDateTime, Arg.Any<CancellationToken>());
        Assert.Equal(fingerprints, _server.HeartbeatInventoryFingerprintsJson);
    }

    [Fact]
    public async Task ChangedSection_IsTheOnlyOneRewritten()
    {
        await BeatAsync(Inventory());
        _time.Advance(TimeSpan.FromSeconds(30));

        await BeatAsync(Inventory(serviceStatus: "stopped"));

        var writes = WriteCounts();
        Assert.Equal(2, writes[nameof(IServerHeartbeatRepository.ReplaceServicesAsync)]);
        Assert.All(writes.Where(write => write.Key != nameof(IServerHeartbeatRepository.ReplaceServicesAsync)),
            write => Assert.Equal(1, write.Value));
        await _heartbeatRepo.Received(1).ReplaceServicesAsync(_server.Id,
            Arg.Is<List<ServiceInfo>>(services => services.Single().Status == "stopped"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangedPortsentry_ReadsThePreviousCountersAndStillAlerts()
    {
        _heartbeatRepo.GetSecurityCountersAsync(_server.Id, Arg.Any<CancellationToken>()).Returns((0, 0), (2, 1));
        await BeatAsync(Inventory(blockedCount: 2));
        _time.Advance(TimeSpan.FromSeconds(30));
        _alertProxy.ClearReceivedCalls();

        await BeatAsync(Inventory(blockedCount: 5));

        Assert.Equal(2, Calls(nameof(IServerHeartbeatRepository.GetSecurityCountersAsync)));
        Assert.Equal(2, Calls(nameof(IServerHeartbeatRepository.ReplacePortsentryDataAsync)));
        await _alertProxy.Received(1).SendCoreAsync("AlertTriggered",
            Arg.Is<object?[]>(arguments => arguments.Length == 1
                && ((AlertTriggeredDto)arguments[0]!).Message == "3 new IP(s) blocked by PortSentry"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FingerprintsOlderThanTheFullRewriteInterval_RewriteEverySection()
    {
        await BeatAsync(Inventory());
        _time.Advance(HeartbeatInventoryFingerprints.FullRewriteInterval - TimeSpan.FromSeconds(1));
        await BeatAsync(Inventory());
        Assert.All(WriteCounts(), write => Assert.Equal(1, write.Value));

        _time.Advance(TimeSpan.FromSeconds(1));
        await BeatAsync(Inventory());

        Assert.All(WriteCounts(), write => Assert.Equal(2, write.Value));
    }

    [Fact]
    public async Task UnreadableFingerprints_RewriteEverySection()
    {
        _server.HeartbeatInventoryFingerprintsJson = "{not json";

        await BeatAsync(Inventory());

        Assert.All(WriteCounts(), write => Assert.Equal(1, write.Value));
        Assert.StartsWith("{\"FullRewriteAt\"", _server.HeartbeatInventoryFingerprintsJson, StringComparison.Ordinal);
    }
}
