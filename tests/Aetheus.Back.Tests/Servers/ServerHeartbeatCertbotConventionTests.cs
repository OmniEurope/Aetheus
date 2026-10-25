// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Certbot;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Servers.Events;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Persist renewal conventions and rehearsal results, reporting each new failed rehearsal
/// once.</summary>
public sealed class ServerHeartbeatCertbotConventionTests
{
    private static readonly DateTime CheckedAt = new(2026, 9, 18, 12, 8, 8, DateTimeKind.Utc);

    private readonly IServerRepository _repo = Substitute.For<IServerRepository>();
    private readonly IServerHeartbeatRepository _heartbeatRepo = Substitute.For<IServerHeartbeatRepository>();
    private readonly IDomainEventDispatcher _events = Substitute.For<IDomainEventDispatcher>();
    private readonly ServerService _sut;

    public ServerHeartbeatCertbotConventionTests()
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        hub.Clients.Returns(Substitute.For<IHubClients>());
        var alertHub = Substitute.For<IHubContext<AlertHub>>();
        alertHub.Clients.Returns(Substitute.For<IHubClients>());
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(new Server { Id = 1, Name = "web", Hostname = "web" });
        _sut = new ServerService(_repo, _heartbeatRepo, hub, alertHub, Substitute.For<IAuditService>(),
            Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>(),
            Options.Create(new BackgroundServicesOptions()), TimeProvider.System, Substitute.For<IDbTransactionScope>(),
            domainEvents: _events);
    }

    [Fact]
    public async Task Certificates_KeepTheirRenewalSettingsAndConventionVerdict()
    {
        await _sut.ProcessHeartbeatAsync(1, Heartbeat(succeeded: true), ct: TestContext.Current.CancellationToken);

        await _heartbeatRepo.Received(1).ReplaceCertbotCertificatesAsync(1, Arg.Is<List<CertbotCertificate>>(certificates =>
            certificates.Count == 1
            && certificates[0].Authenticator == "apache"
            && certificates[0].WebrootPath == string.Empty
            && certificates[0].RenewalConvention == CertbotRenewalConvention.NotWebroot), Arg.Any<CancellationToken>());
        await _heartbeatRepo.Received(1).ReplaceCertbotStateAsync(1, Arg.Is<CertbotState?>(state =>
            state != null && state.RenewalCheckedAt == CheckedAt && state.RenewalCheckSucceeded == true),
            Arg.Any<CancellationToken>());
        _events.DidNotReceive().Publish(Arg.Any<CertbotRenewalCheckFailedEvent>());
    }

    [Fact]
    public async Task NewlyRecordedFailedRenewalCheck_IsPublished()
    {
        _heartbeatRepo.GetCertbotRenewalCheckedAtAsync(1, Arg.Any<CancellationToken>())
            .Returns(CheckedAt.AddDays(-7));

        await _sut.ProcessHeartbeatAsync(1, Heartbeat(succeeded: false), ct: TestContext.Current.CancellationToken);

        _events.Received(1).Publish(Arg.Is<CertbotRenewalCheckFailedEvent>(e => e.ServerId == 1 && e.ServerName == "web" && e.CheckedAt == CheckedAt));
    }

    [Fact]
    public async Task R3_56_TheFailedLineagesAndTheirReasons_AreStoredWithTheState_AndReadBackForThePage()
    {
        var heartbeat = Heartbeat(succeeded: false);
        heartbeat = heartbeat with
        {
            Certbot = heartbeat.Certbot with
            {
                RenewalCheckFailures = [new CertbotRenewalFailureDto { Name = "shop.example.com", Reason = "Some challenges have failed." }]
            }
        };
        CertbotState? stored = null;
        await _heartbeatRepo.ReplaceCertbotStateAsync(1, Arg.Do<CertbotState?>(state => stored = state), Arg.Any<CancellationToken>());

        await _sut.ProcessHeartbeatAsync(1, heartbeat, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        var mapped = ServerDataMapper.MapCertbotDataDto(new Server { CertbotState = stored });
        var failure = Assert.Single(mapped.RenewalCheckFailures);
        Assert.Equal("shop.example.com", failure.Name);
        Assert.Equal("Some challenges have failed.", failure.Reason);
    }

    [Fact]
    public async Task R1_7_TheOutputTailOfAFailedCheck_IsStoredWithTheState_AndReadBackForThePage()
    {
        var heartbeat = Heartbeat(succeeded: false);
        heartbeat = heartbeat with
        {
            Certbot = heartbeat.Certbot with
            {
                RenewalCheckOutputTail = ["certbot renew --dry-run ran for 612 s and exited with code 1", "Another instance of Certbot is already running."]
            }
        };
        CertbotState? stored = null;
        await _heartbeatRepo.ReplaceCertbotStateAsync(1, Arg.Do<CertbotState?>(state => stored = state), Arg.Any<CancellationToken>());

        await _sut.ProcessHeartbeatAsync(1, heartbeat, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        var mapped = ServerDataMapper.MapCertbotDataDto(new Server { CertbotState = stored });
        Assert.Equal(["certbot renew --dry-run ran for 612 s and exited with code 1", "Another instance of Certbot is already running."], mapped.RenewalCheckOutputTail);
    }

    [Fact]
    public async Task R1_7_APassedCheckStoresNoTail_AndAnUnreadableColumnReadsAsNone()
    {
        CertbotState? stored = null;
        await _heartbeatRepo.ReplaceCertbotStateAsync(1, Arg.Do<CertbotState?>(state => stored = state), Arg.Any<CancellationToken>());

        await _sut.ProcessHeartbeatAsync(1, Heartbeat(succeeded: true), ct: TestContext.Current.CancellationToken);

        Assert.Null(stored!.RenewalCheckOutputTailJson);
        Assert.Empty(ServerDataMapper.MapCertbotDataDto(new Server { CertbotState = new CertbotState { RenewalCheckOutputTailJson = "{not json" } }).RenewalCheckOutputTail);
    }

    [Fact]
    public async Task R2_TheProxyRulesTheHelperRecorded_AreStoredWithTheState_AndReadBackForThePage()
    {
        var heartbeat = Heartbeat(succeeded: true);
        heartbeat = heartbeat with { Certbot = heartbeat.Certbot with { AcmeProxyRules = ["/etc/apache2/sites-enabled/shop.conf:12"] } };
        CertbotState? stored = null;
        await _heartbeatRepo.ReplaceCertbotStateAsync(1, Arg.Do<CertbotState?>(state => stored = state), Arg.Any<CancellationToken>());

        await _sut.ProcessHeartbeatAsync(1, heartbeat, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(["/etc/apache2/sites-enabled/shop.conf:12"], ServerDataMapper.MapCertbotDataDto(new Server { CertbotState = stored }).AcmeProxyRules);
    }

    [Fact]
    public async Task R2_NoProxyRule_StoresNothing_AndAnUnreadableColumnReadsAsNone()
    {
        CertbotState? stored = null;
        await _heartbeatRepo.ReplaceCertbotStateAsync(1, Arg.Do<CertbotState?>(state => stored = state), Arg.Any<CancellationToken>());

        await _sut.ProcessHeartbeatAsync(1, Heartbeat(succeeded: true), ct: TestContext.Current.CancellationToken);

        Assert.Null(stored!.AcmeProxyRulesJson);
        Assert.Empty(ServerDataMapper.MapCertbotDataDto(new Server { CertbotState = new CertbotState { AcmeProxyRulesJson = "{not json" } }).AcmeProxyRules);
    }

    [Fact]
    public async Task R3_56_APassedCheck_StoresNoFailureList()
    {
        CertbotState? stored = null;
        await _heartbeatRepo.ReplaceCertbotStateAsync(1, Arg.Do<CertbotState?>(state => stored = state), Arg.Any<CancellationToken>());

        await _sut.ProcessHeartbeatAsync(1, Heartbeat(succeeded: true), ct: TestContext.Current.CancellationToken);

        Assert.Null(stored!.RenewalCheckFailuresJson);
        Assert.Empty(ServerDataMapper.MapCertbotDataDto(new Server { CertbotState = new CertbotState { RenewalCheckFailuresJson = "{not json" } }).RenewalCheckFailures);
    }

    [Fact]
    public async Task AlreadyKnownFailedRenewalCheck_IsNotPublishedAgain()
    {
        _heartbeatRepo.GetCertbotRenewalCheckedAtAsync(1, Arg.Any<CancellationToken>()).Returns(CheckedAt);

        await _sut.ProcessHeartbeatAsync(1, Heartbeat(succeeded: false), ct: TestContext.Current.CancellationToken);

        _events.DidNotReceive().Publish(Arg.Any<CertbotRenewalCheckFailedEvent>());
    }

    [Fact]
    public async Task CertbotNotInstalled_ClearsTheState()
    {
        await _sut.ProcessHeartbeatAsync(1, new ServerHeartbeatDto(), ct: TestContext.Current.CancellationToken);

        await _heartbeatRepo.Received(1).ReplaceCertbotStateAsync(1, null, Arg.Any<CancellationToken>());
        _events.DidNotReceive().Publish(Arg.Any<CertbotRenewalCheckFailedEvent>());
    }

    [Fact]
    public async Task FailedRenewalCheckEvent_ReachesTheNotificationRules()
    {
        var notifications = Substitute.For<INotificationService>();
        var handler = new CertbotRenewalCheckNotificationHandler(notifications);

        await handler.HandleAsync(new CertbotRenewalCheckFailedEvent(1, "web", CheckedAt), TestContext.Current.CancellationToken);

        await notifications.Received(1).SendEventAsync("certbot.renewal-check.failed", Arg.Any<object>(), Arg.Any<CancellationToken>());
        var payload = notifications.ReceivedCalls().Select(call => call.GetArguments()[1]).Single();
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(payload));
        Assert.Equal("web", json.RootElement.GetProperty("ServerName").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("ServerId").GetInt32());
    }

    private static ServerHeartbeatDto Heartbeat(bool succeeded) => new()
    {
        Certbot = new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = CheckedAt,
            RenewalCheckSucceeded = succeeded,
            Certificates =
            [
                new CertbotCertificateDto
                {
                    Name = "random.example.com",
                    Domains = ["random.example.com"],
                    ExpiryDate = CheckedAt.AddDays(60),
                    Authenticator = "apache",
                    RenewalConvention = CertbotRenewalConvention.NotWebroot
                }
            ]
        }
    };
}
