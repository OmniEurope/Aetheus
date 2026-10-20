// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AppMonitoring;

public sealed class AppMonitoringServiceCrudTests
{
    private readonly IAppMonitoringRepository _repo = Substitute.For<IAppMonitoringRepository>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IEntityChangeNotifier _notifier = Substitute.For<IEntityChangeNotifier>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly AppMonitoringService _service;

    public AppMonitoringServiceCrudTests()
    {
        _repo.GetProjectOrgIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<int>>().ToDictionary(id => id, _ => 9));
        _repo.GetUptimeWindowsAsync(
                Arg.Any<IReadOnlyCollection<int>>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, AppUptimeWindowCounts>());
        _service = new AppMonitoringService(
            _repo, _notifications, _notifier, _time, NullLogger<AppMonitoringService>.Instance);
    }

    [Fact]
    public async Task GetAppsForProject_MapsNavigationFieldsAndThreeHonestUptimeWindows()
    {
        var app = new MonitoredApp
        {
            Id = 2,
            ProjectId = 4,
            Name = "api",
            ProbeUrl = "https://api.example/health",
            Project = new Project { Id = 4, Name = "Platform" },
            Environment = new Aetheus.Back.Data.Entities.Environment { Id = 5, Name = "prod" },
            EnvironmentId = 5,
            Server = new Server { Id = 7, Name = "web-01" },
            ServerId = 7,
            IngestKeyHash = "hash",
            IngestKeyExpiresAt = _time.GetUtcNow().UtcDateTime.AddDays(30),
            IngestDroppedCount = 3
        };
        _repo.GetAppsByProjectAsync(4, Arg.Any<CancellationToken>()).Returns([app]);
        _repo.GetUptimeWindowsAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 2 })),
                _time.GetUtcNow().UtcDateTime,
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, AppUptimeWindowCounts>
            {
                [2] = new(18, 20, 60, 80, 900, 1000)
            });

        var dto = Assert.Single(await _service.GetAppsForProjectAsync(4, ct: TestContext.Current.CancellationToken));

        Assert.Equal("Platform", dto.ProjectName);
        Assert.Equal("prod", dto.EnvironmentName);
        Assert.Equal("web-01", dto.ServerName);
        Assert.Equal(0.9, dto.Uptime24h);
        Assert.Equal(0.75, dto.Uptime7d);
        Assert.Equal(0.9, dto.Uptime90d);
        Assert.True(dto.HasIngestKey);
        Assert.Equal(3, dto.IngestDroppedCount);
        await _repo.Received(1).GetUptimeWindowsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 2 })),
            _time.GetUtcNow().UtcDateTime,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAppAndProjectId_MissingAndExistingPathsPreserveRepositorySemantics()
    {
        _repo.GetAppAsync(99, Arg.Any<CancellationToken>()).Returns((MonitoredApp?)null);
        _repo.GetAppAsync(2, Arg.Any<CancellationToken>())
            .Returns(new MonitoredApp { Id = 2, ProjectId = 4, Name = "api" });
        _repo.GetAppProjectIdAsync(2, Arg.Any<CancellationToken>()).Returns(4);

        Assert.Null(await _service.GetAppAsync(99, ct: TestContext.Current.CancellationToken));
        Assert.Equal("api", (await _service.GetAppAsync(2, ct: TestContext.Current.CancellationToken))!.Name);
        Assert.Equal(4, await _service.GetAppProjectIdAsync(2, ct: TestContext.Current.CancellationToken));
    }

    /// <summary> An app registered without a server is probed by the backend, yet runs on a fleet
    /// server; the list names that server when one of its Apache virtual hosts serves the probe
    /// host. A registered server is never replaced, and an unknown host stays "off-fleet".
    /// </summary>
    [Fact]
    public async Task GetAppsForProject_NamesTheServerWhoseVirtualHostServesTheProbeUrl()
    {
        var backendProbed = new MonitoredApp { Id = 3, ProjectId = 4, Name = "aetheus", ProbeUrl = "https://Aetheus-API.example.com/health" };
        var unknownHost = new MonitoredApp { Id = 5, ProjectId = 4, Name = "elsewhere", ProbeUrl = "https://other.example.org/" };
        var registered = new MonitoredApp
        {
            Id = 6,
            ProjectId = 4,
            Name = "agent-probed",
            ProbeUrl = "https://aetheus-api.example.com/",
            ServerId = 9,
            Server = new Server { Id = 9, Name = "web-09" }
        };
        _repo.GetAppsByProjectAsync(4, Arg.Any<CancellationToken>()).Returns([backendProbed, unknownHost, registered]);
        _repo.GetVirtualHostServersAsync(
                4,
                Arg.Is<IReadOnlyCollection<string>>(hosts => hosts.Order().SequenceEqual(new[] { "aetheus-api.example.com", "other.example.org" })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, AppHostingServer> { ["aetheus-api.example.com"] = new(7, "vps2577917") });

        var apps = await _service.GetAppsForProjectAsync(4, ct: TestContext.Current.CancellationToken);

        var hosted = apps.Single(app => app.Id == 3).HostingServer;
        Assert.NotNull(hosted);
        Assert.Equal(7, hosted.ServerId);
        Assert.Equal("vps2577917", hosted.ServerName);
        Assert.Null(apps.Single(app => app.Id == 3).ServerId);
        Assert.Null(apps.Single(app => app.Id == 5).HostingServer);
        Assert.Null(apps.Single(app => app.Id == 6).HostingServer);
        Assert.Equal("web-09", apps.Single(app => app.Id == 6).ServerName);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://example.com")]
    public async Task CreateApp_InvalidProbeUrl_RejectsBeforePersistence(string url)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _service.CreateAppAsync(4,
            new CreateMonitoredAppRequest { Name = "api", ProjectId = 4, ProbeUrl = url }, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddAppAsync(Arg.Any<MonitoredApp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateApp_ValidRequest_TrimsClampsPersistsAndBroadcastsWithinOrganization()
    {
        MonitoredApp? persisted = null;
        _repo.AddAppAsync(Arg.Any<MonitoredApp>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            persisted = call.Arg<MonitoredApp>();
            persisted.Id = 12;
            return Task.CompletedTask;
        });

        var dto = await _service.CreateAppAsync(4, new CreateMonitoredAppRequest
        {
            ProjectId = 4,
            Name = "  api  ",
            ProbeUrl = "  https://api.example/health  ",
            ProbeIntervalSeconds = 1,
            ProbeTimeoutSeconds = 500,
            ExpectedStatusCode = 999,
            FailureThreshold = 0,
            RecoveryThreshold = 99,
            Enabled = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(persisted);
        Assert.Equal("api", persisted.Name);
        Assert.Equal("https://api.example/health", persisted.ProbeUrl);
        Assert.Equal(15, persisted.ProbeIntervalSeconds);
        Assert.Equal(120, persisted.ProbeTimeoutSeconds);
        Assert.Equal(599, persisted.ExpectedStatusCode);
        Assert.Equal(1, persisted.FailureThreshold);
        Assert.Equal(20, persisted.RecoveryThreshold);
        Assert.Equal(AppHealthStatus.Unknown, persisted.CurrentStatus);
        Assert.Equal(12, dto.Id);
        await _notifier.Received(1).BroadcastAsync(ResourceType.Project, 4, EntityChangeOps.Created, Arg.Any<CancellationToken>(), 9);
    }

    [Fact]
    public async Task CreateApp_ServerOfAnotherOrganization_RejectsBeforePersistence()
    {
        // The project belongs to organization 9 (constructor stub); the chosen server to organization 2.
        _repo.GetServerOrganizationIdAsync(30, Arg.Any<CancellationToken>()).Returns(2);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.CreateAppAsync(4,
            new CreateMonitoredAppRequest { Name = "api", ProjectId = 4, ServerId = 30, ProbeUrl = "http://10.0.0.1/" },
            ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddAppAsync(Arg.Any<MonitoredApp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateApp_EnvironmentOfAnotherProject_RejectsBeforeSaving()
    {
        _repo.GetAppForUpdateAsync(2, Arg.Any<CancellationToken>()).Returns(new MonitoredApp { Id = 2, ProjectId = 4, Name = "api" });
        _repo.EnvironmentIsUsableByProjectAsync(50, 4, Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.UpdateAppAsync(2,
            new UpdateMonitoredAppRequest { Name = "api", EnvironmentId = 50 }, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateApp_ServerAndEnvironmentOfTheProject_ArePersisted()
    {
        _repo.GetServerOrganizationIdAsync(30, Arg.Any<CancellationToken>()).Returns(9);
        _repo.EnvironmentIsUsableByProjectAsync(50, 4, Arg.Any<CancellationToken>()).Returns(true);

        await _service.CreateAppAsync(4,
            new CreateMonitoredAppRequest { Name = "api", ProjectId = 4, ServerId = 30, EnvironmentId = 50, ProbeUrl = "http://10.0.0.1/" },
            ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).AddAppAsync(Arg.Is<MonitoredApp>(app => app.ServerId == 30 && app.EnvironmentId == 50), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(7, "https://api.example/health", null)]
    [InlineData(8, "https://api.example/health", 8)]
    [InlineData(7, "http://10.0.0.1/", 7)]
    [InlineData(null, "http://10.0.0.1/", null)]
    public async Task GetProbeServerChange_OnlyANewAgentTargetNeedsTheServer(int? serverId, string probeUrl, int? expected)
    {
        _repo.GetAppAsync(2, Arg.Any<CancellationToken>())
            .Returns(new MonitoredApp { Id = 2, ProjectId = 4, ServerId = 7, ProbeUrl = "https://api.example/health" });

        var server = await _service.GetProbeServerChangeAsync(2,
            new UpdateMonitoredAppRequest { Name = "api", ServerId = serverId, ProbeUrl = probeUrl },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, server);
    }

    [Fact]
    public async Task UpdateApp_MissingReturnsNull_ExistingMutatesAndBroadcasts()
    {
        _repo.GetAppForUpdateAsync(99, Arg.Any<CancellationToken>()).Returns((MonitoredApp?)null);
        Assert.Null(await _service.UpdateAppAsync(99, new UpdateMonitoredAppRequest { Name = "missing" }, ct: TestContext.Current.CancellationToken));

        var app = new MonitoredApp { Id = 2, ProjectId = 4, Name = "old" };
        _repo.GetAppForUpdateAsync(2, Arg.Any<CancellationToken>()).Returns(app);
        var dto = await _service.UpdateAppAsync(2, new UpdateMonitoredAppRequest
        {
            Name = "  renamed ",
            ProbeUrl = " ",
            ProbeIntervalSeconds = 4000,
            ProbeTimeoutSeconds = 0,
            ExpectedStatusCode = 50,
            FailureThreshold = 30,
            RecoveryThreshold = 0,
            Enabled = false
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(dto);
        Assert.Equal("renamed", app.Name);
        Assert.Null(app.ProbeUrl);
        Assert.Equal(3600, app.ProbeIntervalSeconds);
        Assert.Equal(1, app.ProbeTimeoutSeconds);
        Assert.Equal(100, app.ExpectedStatusCode);
        Assert.Equal(20, app.FailureThreshold);
        Assert.Equal(1, app.RecoveryThreshold);
        Assert.False(app.Enabled);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(ResourceType.Project, 4, EntityChangeOps.Updated, Arg.Any<CancellationToken>(), 9);
    }

    [Fact]
    public async Task DeleteApp_ExistingRemovesAndBroadcasts_MissingReturnsFalse()
    {
        _repo.GetAppForUpdateAsync(99, Arg.Any<CancellationToken>()).Returns((MonitoredApp?)null);
        Assert.False(await _service.DeleteAppAsync(99, ct: TestContext.Current.CancellationToken));

        var app = new MonitoredApp { Id = 2, ProjectId = 4 };
        _repo.GetAppForUpdateAsync(2, Arg.Any<CancellationToken>()).Returns(app);
        Assert.True(await _service.DeleteAppAsync(2, ct: TestContext.Current.CancellationToken));

        await _repo.Received(1).RemoveAppAsync(app, Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(ResourceType.Project, 4, EntityChangeOps.Deleted, Arg.Any<CancellationToken>(), 9);
    }

    [Fact]
    public async Task GetSamples_ClampsWindowAndMapsEveryObservableField()
    {
        var expectedSince = _time.GetUtcNow().UtcDateTime.AddHours(-168);
        _repo.GetSamplesSinceAsync(2, expectedSince, Arg.Any<CancellationToken>()).Returns(
        [
            new AppHealthSample
            {
                MonitoredAppId = 2, Timestamp = _time.GetUtcNow().UtcDateTime,
                IsUp = false, ResponseTimeMs = 250, StatusCode = 503, Error = "unavailable"
            }
        ]);

        var sample = Assert.Single(await _service.GetSamplesAsync(2, 999, ct: TestContext.Current.CancellationToken));

        Assert.False(sample.IsUp);
        Assert.Equal(250, sample.ResponseTimeMs);
        Assert.Equal(503, sample.StatusCode);
        Assert.Equal("unavailable", sample.Error);
    }

    [Fact]
    public async Task ProbeConfigsAndServerIdMap_DelegateWithoutLosingOwnershipData()
    {
        _repo.GetAppsForServerAsync(7, Arg.Any<CancellationToken>()).Returns(
        [
            new MonitoredApp
            {
                Id = 2, ProbeUrl = null, ProbeIntervalSeconds = 30,
                ProbeTimeoutSeconds = 4, ExpectedStatusCode = 204
            }
        ]);
        var ownership = new Dictionary<int, int?> { [2] = 7, [3] = null };
        _repo.GetAppServerIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(ownership);

        var config = Assert.Single(await _service.GetProbeConfigsForServerAsync(7, ct: TestContext.Current.CancellationToken));
        var mappedOwnership = await _service.GetAppServerIdsAsync([2, 3], ct: TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, config.ProbeUrl);
        Assert.Equal(204, config.ExpectedStatusCode);
        Assert.Same(ownership, mappedOwnership);
    }

    [Fact]
    public async Task GetSummary_EmptyScopeShortCircuits_OtherwiseReturnsEveryAppAndAvailableVisitors()
    {
        var empty = await _service.GetSummaryAsync([], ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, empty.TotalCount);
        await _repo.DidNotReceive().GetAppsForSummaryAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>());

        var apps = new List<MonitoredApp>
        {
            new() { Id = 1, ProjectId = 4, Name = "up", CurrentStatus = AppHealthStatus.Up, AnalyticsEnabled = true },
            new() { Id = 2, ProjectId = 4, Name = "down-old", CurrentStatus = AppHealthStatus.Down, LastStatusChangeAt = _time.GetUtcNow().UtcDateTime.AddHours(-2) },
            new() { Id = 3, ProjectId = 5, Name = "degraded-new", CurrentStatus = AppHealthStatus.Degraded, LastStatusChangeAt = _time.GetUtcNow().UtcDateTime.AddMinutes(-5), Project = new Project { Name = "Web" } },
            new() { Id = 4, ProjectId = 5, Name = "unknown", CurrentStatus = AppHealthStatus.Unknown }
        };
        _repo.GetAppsForSummaryAsync(null, Arg.Any<CancellationToken>()).Returns(apps);
        _repo.GetActiveVisitorCountsAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1 })),
                _time.GetUtcNow().UtcDateTime.AddMinutes(-5),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [1] = 3 });
        _repo.GetAudienceTotalsAsync(default!, default, default, default, TestContext.Current.CancellationToken).ReturnsForAnyArgs(new List<AppAudiencePeriodTotal>());

        var summary = await _service.GetSummaryAsync(null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, summary.TotalCount);
        Assert.Equal(1, summary.UpCount);
        Assert.Equal(1, summary.DownCount);
        Assert.Equal(1, summary.DegradedCount);
        Assert.Equal(1, summary.UnknownCount);
        Assert.Equal(4, summary.Applications.Count);
        Assert.Equal(3, summary.Applications.Single(app => app.Name == "up").OnlineVisitorCount);
        Assert.Null(summary.Applications.Single(app => app.Name == "unknown").OnlineVisitorCount);
        Assert.Equal(["degraded-new", "down-old"], summary.Troubled.Select(x => x.Name));
        Assert.Equal("Web", summary.Troubled[0].ProjectName);
    }

    [Fact]
    public async Task R468_GetSummary_AddsUpTheAudienceOfTheApplicationsThatMeasureIt()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _repo.GetAppsForSummaryAsync(null, Arg.Any<CancellationToken>()).Returns(
        [
            new MonitoredApp { Id = 1, ProjectId = 1, Name = "site", AnalyticsEnabled = true },
            new MonitoredApp { Id = 2, ProjectId = 2, Name = "shop", AnalyticsEnabled = true },
            new MonitoredApp { Id = 3, ProjectId = 2, Name = "api" }
        ]);
        _repo.GetActiveVisitorCountsAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1, 2 })), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [1] = 3, [2] = 4 });
        _repo.GetAudienceTotalsAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1, 2 })),
                DateOnly.FromDateTime(now), AppWebAnalyticsRepository.WeekStart(now), new DateOnly(now.Year, now.Month, 1),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new AppAudiencePeriodTotal(AnalyticsPeriodKind.Day, 5, 1, 6, 20),
                new AppAudiencePeriodTotal(AnalyticsPeriodKind.Month, 40, 7, 90, 600)
            ]);

        var audience = (await _service.GetSummaryAsync(null, ct: TestContext.Current.CancellationToken)).Audience;

        Assert.NotNull(audience);
        Assert.Equal((2, 7, 5, 0, 40), (audience.ApplicationCount, audience.OnlineVisitors, audience.VisitorsToday, audience.VisitorsThisWeek, audience.VisitorsThisMonth));
        Assert.Equal((7, 90, 600L), (audience.AuthenticatedVisitorsThisMonth, audience.SessionsThisMonth, audience.PageViewsThisMonth));
    }

    [Fact]
    public async Task R468_GetSummary_HasNoAudience_WhenNoApplicationMeasuresIt()
    {
        _repo.GetAppsForSummaryAsync(null, Arg.Any<CancellationToken>()).Returns([new MonitoredApp { Id = 3, ProjectId = 2, Name = "api" }]);

        var summary = await _service.GetSummaryAsync(null, ct: TestContext.Current.CancellationToken);

        Assert.Null(summary.Audience);
        await _repo.DidNotReceiveWithAnyArgs().GetAudienceTotalsAsync(default!, default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetSummary_LastEvent_IsTheLatestOfOtlpAndWebAnalytics_AndNullWhenNothingArrived()
    {
        // "collected, then stopped" must be told apart from "never configured".
        var otlp = new DateTime(2026, 8, 28, 21, 57, 0, DateTimeKind.Utc);
        var web = otlp.AddHours(3);
        _repo.GetAppsForSummaryAsync(null, Arg.Any<CancellationToken>()).Returns(
        [
            new MonitoredApp { Id = 1, ProjectId = 1, Name = "both", LastIngestAt = otlp, AnalyticsLastIngestAt = web },
            new MonitoredApp { Id = 2, ProjectId = 1, Name = "otlp-only", LastIngestAt = otlp },
            new MonitoredApp { Id = 3, ProjectId = 1, Name = "silent" }
        ]);

        var summary = await _service.GetSummaryAsync(null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(web, summary.Applications.Single(app => app.Name == "both").LastEventAt);
        Assert.Equal(otlp, summary.Applications.Single(app => app.Name == "otlp-only").LastEventAt);
        Assert.Null(summary.Applications.Single(app => app.Name == "silent").LastEventAt);
    }
}
