// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>
/// The read side of the app-monitoring repository: the RBAC scopes, the probe-selection filters,
/// the ingest-key resolution window and the three uptime rollups. The retention writes are covered
/// by <see cref="AppMonitoringRetentionTests"/>; the storage-size read is a raw PostgreSQL query and
/// is asserted here only on its non-relational short circuit.
/// </summary>
public sealed class AppMonitoringRepositoryQueryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db;
    private readonly AppMonitoringRepository _repository;

    public AppMonitoringRepositoryQueryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options);
        _repository = new AppMonitoringRepository(_db);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SaveAsync() => await _db.SaveChangesAsync(Ct);

    private static MonitoredApp App(
        int id, string name, int projectId = 10, int? environmentId = null, int? serverId = null,
        bool enabled = true, string? probeUrl = "https://app.test/health") =>
        new()
        {
            Id = id,
            Name = name,
            ProjectId = projectId,
            EnvironmentId = environmentId,
            ServerId = serverId,
            Enabled = enabled,
            ProbeUrl = probeUrl
        };

    private static AppHealthSample Sample(int id, int appId, DateTime at, bool up, int? responseMs = null) =>
        new() { Id = id, MonitoredAppId = appId, Timestamp = at, IsUp = up, ResponseTimeMs = responseMs };

    private static AppHealthHourly Hourly(int id, int appId, DateTime hour, int up, int total) =>
        new() { Id = id, MonitoredAppId = appId, HourUtc = hour, UpCount = up, SampleCount = total };

    // ---------- app lookups ----------

    [Fact]
    public async Task GetAppsByProjectAsync_OrdersByNameAndMaterializesTheEnvironmentAndServer()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Environments.Add(new Environment { Id = 20, Name = "staging", ProjectId = 10 });
        _db.Servers.Add(new Server { Id = 30, Name = "vps-1" });
        _db.MonitoredApps.AddRange(
            App(1, "zulu", environmentId: 20, serverId: 30),
            App(2, "alpha"),
            App(3, "elsewhere", projectId: 11));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var apps = await _repository.GetAppsByProjectAsync(10, Ct);

        Assert.Equal(["alpha", "zulu"], apps.Select(app => app.Name));
        Assert.Equal("staging", apps[1].Environment!.Name);
        Assert.Equal("vps-1", apps[1].Server!.Name);
    }

    [Fact]
    public async Task GetAppAsync_LoadsTheOwnerGraphOrReturnsNull()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.MonitoredApps.Add(App(1, "web"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var app = await _repository.GetAppAsync(1, Ct);

        Assert.Equal("aetheus", app!.Project.Name);
        Assert.Empty(_db.ChangeTracker.Entries<MonitoredApp>());
        Assert.Null(await _repository.GetAppAsync(404, Ct));
    }

    [Fact]
    public async Task GetAppForUpdateAsync_ReturnsATrackedAppThatSaveChangesPersists()
    {
        _db.MonitoredApps.Add(App(1, "web"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var app = await _repository.GetAppForUpdateAsync(1, Ct);
        app!.Name = "renamed";
        await _repository.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal("renamed", (await _repository.GetAppForUpdateAsync(1, Ct))!.Name);
        Assert.Null(await _repository.GetAppForUpdateAsync(404, Ct));
    }

    [Fact]
    public async Task GetAppProjectIdAsync_ReadsTheOwnerOrNullForAnUnknownApp()
    {
        _db.MonitoredApps.Add(App(1, "web", projectId: 42));
        await SaveAsync();

        Assert.Equal(42, await _repository.GetAppProjectIdAsync(1, Ct));
        Assert.Null(await _repository.GetAppProjectIdAsync(404, Ct));
    }

    [Fact]
    public async Task GetProjectOrgIdsAsync_MapsOnlyTheRequestedProjects()
    {
        _db.Projects.AddRange(
            new Project { Id = 10, Name = "a", OrganizationId = 7 },
            new Project { Id = 11, Name = "b", OrganizationId = 8 });
        await SaveAsync();

        var map = await _repository.GetProjectOrgIdsAsync([10, 404], Ct);

        Assert.Equal(7, Assert.Contains(10, map));
        Assert.DoesNotContain(11, map);
    }

    [Fact]
    public async Task GetBackendProbedAppsAsync_KeepsOnlyEnabledOffFleetAppsWithAProbeUrl()
    {
        _db.MonitoredApps.AddRange(
            App(1, "kept"),
            App(2, "disabled", enabled: false),
            App(3, "on-fleet", serverId: 30),
            App(4, "no-url", probeUrl: null));
        await SaveAsync();

        var apps = await _repository.GetBackendProbedAppsAsync(Ct);

        Assert.Equal(["kept"], apps.Select(app => app.Name));
    }

    [Fact]
    public async Task GetAppsForServerAsync_KeepsOnlyThatServersEnabledProbesOrderedById()
    {
        _db.MonitoredApps.AddRange(
            App(3, "third", serverId: 30),
            App(1, "first", serverId: 30),
            App(2, "disabled", serverId: 30, enabled: false),
            App(4, "no-url", serverId: 30, probeUrl: null),
            App(5, "other-server", serverId: 31));
        await SaveAsync();

        var apps = await _repository.GetAppsForServerAsync(30, Ct);

        Assert.Equal(["first", "third"], apps.Select(app => app.Name));
    }

    [Fact]
    public async Task GetAppServerIdsAsync_MapsTheRequestedAppsIncludingTheOffFleetNull()
    {
        _db.MonitoredApps.AddRange(
            App(1, "on-fleet", serverId: 30),
            App(2, "off-fleet"),
            App(3, "not-requested", serverId: 31));
        await SaveAsync();

        var map = await _repository.GetAppServerIdsAsync([1, 2], Ct);

        Assert.Equal(30, Assert.Contains(1, map));
        Assert.Null(Assert.Contains(2, map));
        Assert.DoesNotContain(3, map);
    }

    [Fact]
    public async Task GetAppsByIdsForUpdateAsync_ReturnsTrackedAppsKeyedById()
    {
        _db.MonitoredApps.AddRange(App(1, "one"), App(2, "two"), App(3, "three"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var apps = await _repository.GetAppsByIdsForUpdateAsync([1, 3], Ct);
        apps[1].Name = "renamed";
        await _repository.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();

        Assert.Equal([1, 3], apps.Keys.Order());
        Assert.Equal("renamed", (await _repository.GetAppForUpdateAsync(1, Ct))!.Name);
    }

    [Fact]
    public async Task GetAppsForSummaryAsync_SkipsDisabledAppsAndHonoursTheProjectScope()
    {
        _db.Projects.AddRange(
            new Project { Id = 10, Name = "visible", OrganizationId = 7 },
            new Project { Id = 11, Name = "hidden", OrganizationId = 8 });
        _db.MonitoredApps.AddRange(
            App(1, "kept"),
            App(2, "disabled", enabled: false),
            App(3, "out-of-scope", projectId: 11));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var scoped = await _repository.GetAppsForSummaryAsync([10], Ct);
        var unscoped = await _repository.GetAppsForSummaryAsync(null, Ct);

        Assert.Equal(["kept"], scoped.Select(app => app.Name));
        Assert.Equal("visible", scoped[0].Project.Name);
        Assert.Equal(2, unscoped.Count);
    }

    [Fact]
    public async Task GetActiveVisitorCountsAsync_ShortCircuitsOnAnEmptyRequest()
    {
        Assert.Empty(await _repository.GetActiveVisitorCountsAsync([], Now, Ct));
    }

    // ---------- writes ----------

    [Fact]
    public async Task AddAppAsync_ThenRemoveAppAsync_CommitImmediately()
    {
        var app = App(1, "web");
        await _repository.AddAppAsync(app, Ct);
        Assert.NotNull(await _repository.GetAppForUpdateAsync(1, Ct));

        await _repository.RemoveAppAsync(app, Ct);

        Assert.Null(await _repository.GetAppForUpdateAsync(1, Ct));
    }

    [Fact]
    public async Task AddSamplesAsync_CommitsEveryProbeResultInOneRoundTrip()
    {
        await _repository.AddSamplesAsync(
            [Sample(1, 1, Now, true), Sample(2, 1, Now.AddMinutes(1), false)], Ct);

        Assert.Equal(2, await _db.AppHealthSamples.CountAsync(Ct));
    }

    [Fact]
    public async Task TouchIngestAsync_AccumulatesTheDroppedCountAndStampsTheInstant()
    {
        _db.MonitoredApps.Add(App(1, "web"));
        await SaveAsync();

        await _repository.TouchIngestAsync(1, 3, Now, Ct);
        await _repository.TouchIngestAsync(1, 4, Now.AddMinutes(1), Ct);
        _db.ChangeTracker.Clear();

        var app = await _repository.GetAppForUpdateAsync(1, Ct);
        Assert.Equal(7, app!.IngestDroppedCount);
        Assert.Equal(Now.AddMinutes(1), app.LastIngestAt);
    }

    [Fact]
    public async Task TouchIngestAsync_IsANoOpForAnUnknownApp()
    {
        await _repository.TouchIngestAsync(404, 3, Now, Ct);

        Assert.Empty(_db.MonitoredApps);
    }

    [Fact]
    public async Task GetTelemetryStorageBytesAsync_ReportsZeroWithoutARelationalProvider()
    {
        Assert.Equal(0, await _repository.GetTelemetryStorageBytesAsync(Ct));
    }

    // ---------- samples and uptime ----------

    [Fact]
    public async Task GetSamplesSinceAsync_KeepsTheWindowOfThatAppInChronologicalOrder()
    {
        _db.AppHealthSamples.AddRange(
            Sample(1, 1, Now.AddHours(-2), true),
            Sample(2, 1, Now.AddMinutes(-30), false),
            Sample(3, 1, Now.AddMinutes(-10), true),
            Sample(4, 2, Now.AddMinutes(-10), true));
        await SaveAsync();

        var samples = await _repository.GetSamplesSinceAsync(1, Now.AddHours(-1), Ct);

        Assert.Equal([2, 3], samples.Select(sample => sample.Id));
    }

    [Fact]
    public async Task GetRawUptimeAsync_CountsUpAgainstTotalInsideTheWindow()
    {
        _db.AppHealthSamples.AddRange(
            Sample(1, 1, Now.AddHours(-2), true),
            Sample(2, 1, Now.AddMinutes(-30), true),
            Sample(3, 1, Now.AddMinutes(-10), false),
            Sample(4, 2, Now.AddMinutes(-10), true));
        await SaveAsync();

        Assert.Equal((1, 2), await _repository.GetRawUptimeAsync(1, Now.AddHours(-1), Ct));
    }

    [Fact]
    public async Task GetRawUptimeAsync_ReportsAnEmptyWindowAsZeroOverZero()
    {
        Assert.Equal((0, 0), await _repository.GetRawUptimeAsync(1, Now, Ct));
    }

    [Fact]
    public async Task GetHourlyUptimeAsync_SumsTheRollupBucketsInsideTheWindow()
    {
        _db.AppHealthHourly.AddRange(
            Hourly(1, 1, Now.AddHours(-5), 50, 60),
            Hourly(2, 1, Now.AddHours(-1), 10, 10),
            Hourly(3, 2, Now.AddHours(-1), 99, 99));
        await SaveAsync();

        Assert.Equal((10, 10), await _repository.GetHourlyUptimeAsync(1, Now.AddHours(-2), Ct));
        Assert.Equal((0, 0), await _repository.GetHourlyUptimeAsync(3, Now.AddHours(-2), Ct));
    }

    [Fact]
    public async Task GetUptimeWindowsAsync_ShortCircuitsOnAnEmptyRequest()
    {
        Assert.Empty(await _repository.GetUptimeWindowsAsync([], Now, Ct));
    }

    [Fact]
    public async Task GetUptimeWindowsAsync_CombinesTheHourlyRollupWithTheCurrentHourTail()
    {
        var currentHourStart = new DateTime(Now.Year, Now.Month, Now.Day, Now.Hour, 0, 0, DateTimeKind.Utc);
        _db.AppHealthSamples.AddRange(
            // Inside the current hour: counted in 24h AND appended to the 7d/90d rollups.
            Sample(1, 1, currentHourStart.AddMinutes(5), true),
            Sample(2, 1, currentHourStart.AddMinutes(10), false),
            // Earlier today: inside 24h, already covered by the rollup so not appended again.
            Sample(3, 1, Now.AddHours(-5), true),
            // Older than 24h: outside every raw window.
            Sample(4, 1, Now.AddDays(-2), true));
        _db.AppHealthHourly.AddRange(
            Hourly(1, 1, Now.AddDays(-3), 20, 24),
            Hourly(2, 1, Now.AddDays(-30), 100, 120));
        await SaveAsync();

        var windows = await _repository.GetUptimeWindowsAsync([1], Now, Ct);

        var counts = Assert.Contains(1, windows);
        Assert.Equal(2, counts.Up24h);
        Assert.Equal(3, counts.Total24h);
        Assert.Equal(21, counts.Up7d);
        Assert.Equal(26, counts.Total7d);
        Assert.Equal(121, counts.Up90d);
        Assert.Equal(146, counts.Total90d);
    }

    [Fact]
    public async Task GetUptimeWindowsAsync_ReturnsZeroesForAnAppWithNoDataAtAll()
    {
        var windows = await _repository.GetUptimeWindowsAsync([1, 2], Now, Ct);

        Assert.Equal(new AppUptimeWindowCounts(0, 0, 0, 0, 0, 0), Assert.Contains(2, windows));
    }

    // ---------- deploy target and ingest keys ----------

    [Fact]
    public async Task GetDeployTargetAppAsync_PrefersTheExactEnvironmentMatch()
    {
        _db.MonitoredApps.AddRange(
            App(1, "project-level"),
            App(2, "staging", environmentId: 20));
        await SaveAsync();

        var app = await _repository.GetDeployTargetAppAsync(10, 20, Ct);

        Assert.Equal("staging", app!.Name);
    }

    [Fact]
    public async Task GetDeployTargetAppAsync_FallsBackToTheProjectLevelApp()
    {
        _db.MonitoredApps.AddRange(
            App(1, "project-level"),
            App(2, "other-environment", environmentId: 21));
        await SaveAsync();

        var app = await _repository.GetDeployTargetAppAsync(10, 20, Ct);

        Assert.Equal("project-level", app!.Name);
    }

    [Fact]
    public async Task GetDeployTargetAppAsync_TakesALoneEnvironmentAppWhenNoEnvironmentIsRequested()
    {
        _db.MonitoredApps.Add(App(1, "only-one", environmentId: 20));
        await SaveAsync();

        Assert.Equal("only-one", (await _repository.GetDeployTargetAppAsync(10, null, Ct))!.Name);
    }

    [Fact]
    public async Task GetDeployTargetAppAsync_RefusesToGuessBetweenSeveralEnvironmentApps()
    {
        _db.MonitoredApps.AddRange(
            App(1, "staging", environmentId: 20),
            App(2, "production", environmentId: 21));
        await SaveAsync();

        Assert.Null(await _repository.GetDeployTargetAppAsync(10, null, Ct));
    }

    [Fact]
    public async Task GetDeployTargetAppAsync_IgnoresDisabledAppsAndOtherProjects()
    {
        _db.MonitoredApps.AddRange(
            App(1, "disabled", enabled: false),
            App(2, "other-project", projectId: 11));
        await SaveAsync();

        Assert.Null(await _repository.GetDeployTargetAppAsync(10, null, Ct));
    }

    [Fact]
    public async Task ResolveIngestKeyHashAsync_AcceptsTheCurrentKeyWithNoGraceDeadline()
    {
        _db.MonitoredApps.Add(new MonitoredApp
        {
            Id = 1,
            Name = "web",
            ProjectId = 10,
            IngestKeyHash = "current",
            IngestKeyExpiresAt = Now.AddDays(1)
        });
        await SaveAsync();

        var resolution = await _repository.ResolveIngestKeyHashAsync(["current"], Now, Ct);

        Assert.Equal(1, resolution!.Value.AppId);
        Assert.Null(resolution.Value.ValidUntilUtc);
    }

    [Fact]
    public async Task ResolveIngestKeyHashAsync_AcceptsTheRotatedKeyAndReportsItsGraceDeadline()
    {
        var graceEnd = Now.AddHours(6);
        _db.MonitoredApps.Add(new MonitoredApp
        {
            Id = 1,
            Name = "web",
            ProjectId = 10,
            IngestKeyHash = "current",
            IngestKeyExpiresAt = Now.AddDays(1),
            PreviousIngestKeyHash = "previous",
            PreviousIngestKeyValidUntil = graceEnd
        });
        await SaveAsync();

        var resolution = await _repository.ResolveIngestKeyHashAsync(["previous"], Now, Ct);

        Assert.Equal(graceEnd, resolution!.Value.ValidUntilUtc);
    }

    [Fact]
    public async Task R53_ResolveIngestKeyHashAsync_MatchesAnyCandidate_AndReportsWhichOneMatched()
    {
        var graceEnd = Now.AddHours(6);
        _db.MonitoredApps.Add(new MonitoredApp
        {
            Id = 1,
            Name = "web",
            ProjectId = 10,
            IngestKeyHash = "current-new",
            IngestKeyExpiresAt = Now.AddDays(1),
            PreviousIngestKeyHash = "previous-legacy",
            PreviousIngestKeyValidUntil = graceEnd
        });
        await SaveAsync();

        var legacy = await _repository.ResolveIngestKeyHashAsync(["previous-new", "previous-legacy"], Now, Ct);
        var current = await _repository.ResolveIngestKeyHashAsync(["current-new", "current-legacy"], Now, Ct);

        Assert.Equal(1, legacy!.Value.AppId);
        Assert.Equal(graceEnd, legacy.Value.ValidUntilUtc);
        Assert.Equal("previous-legacy", legacy.Value.MatchedHash);
        Assert.Null(current!.Value.ValidUntilUtc);
        Assert.Equal("current-new", current.Value.MatchedHash);
        Assert.Null(await _repository.ResolveIngestKeyHashAsync(["unknown-new", "unknown-legacy"], Now, Ct));
    }

    [Fact]
    public async Task R2_013_ResolveIngestKeyHashAsync_AcceptsTheSecondPreviousKeyUntilItsOwnDeadline()
    {
        var graceEnd = Now.AddHours(2);
        _db.MonitoredApps.Add(new MonitoredApp
        {
            Id = 1,
            Name = "web",
            ProjectId = 10,
            IngestKeyHash = "current",
            IngestKeyExpiresAt = Now.AddDays(1),
            PreviousIngestKeyHash = "previous",
            PreviousIngestKeyValidUntil = Now.AddHours(6),
            SecondPreviousIngestKeyHash = "second-previous",
            SecondPreviousIngestKeyValidUntil = graceEnd
        });
        await SaveAsync();

        var resolution = await _repository.ResolveIngestKeyHashAsync(["second-previous"], Now, Ct);

        Assert.Equal(1, resolution!.Value.AppId);
        Assert.Equal(graceEnd, resolution.Value.ValidUntilUtc);
        Assert.Null(await _repository.ResolveIngestKeyHashAsync(["second-previous"], graceEnd.AddSeconds(1), Ct));
    }

    [Fact]
    public async Task ResolveIngestKeyHashAsync_RejectsExpiredKeysAndDisabledApps()
    {
        _db.MonitoredApps.AddRange(
            new MonitoredApp
            {
                Id = 1,
                Name = "expired",
                ProjectId = 10,
                IngestKeyHash = "expired-key",
                IngestKeyExpiresAt = Now.AddMinutes(-1)
            },
            new MonitoredApp
            {
                Id = 2,
                Name = "grace-over",
                ProjectId = 10,
                PreviousIngestKeyHash = "stale-key",
                PreviousIngestKeyValidUntil = Now.AddMinutes(-1)
            },
            new MonitoredApp
            {
                Id = 3,
                Name = "disabled",
                ProjectId = 10,
                Enabled = false,
                IngestKeyHash = "disabled-key",
                IngestKeyExpiresAt = Now.AddDays(1)
            });
        await SaveAsync();

        Assert.Null(await _repository.ResolveIngestKeyHashAsync(["expired-key"], Now, Ct));
        Assert.Null(await _repository.ResolveIngestKeyHashAsync(["stale-key"], Now, Ct));
        Assert.Null(await _repository.ResolveIngestKeyHashAsync(["disabled-key"], Now, Ct));
        Assert.Null(await _repository.ResolveIngestKeyHashAsync(["never-issued"], Now, Ct));
    }

    // ---------- analytics ----------

    [Fact]
    public async Task GetAppByAnalyticsSiteIdAsync_MatchesTheSiteIdExactly()
    {
        _db.MonitoredApps.AddRange(
            new MonitoredApp { Id = 1, Name = "web", ProjectId = 10, AnalyticsSiteId = "site-a" },
            new MonitoredApp { Id = 2, Name = "docs", ProjectId = 10, AnalyticsSiteId = "site-b" });
        await SaveAsync();

        Assert.Equal("web", (await _repository.GetAppByAnalyticsSiteIdAsync("site-a", Ct))!.Name);
        Assert.Null(await _repository.GetAppByAnalyticsSiteIdAsync("site-c", Ct));
    }

    [Fact]
    public async Task GetAnalyticsKeyRotationCandidatesAsync_TakesPendingRotationsAndAgedKeysOnly()
    {
        _db.MonitoredApps.AddRange(
            new MonitoredApp
            {
                Id = 1,
                Name = "pending",
                ProjectId = 10,
                AnalyticsVaultName = "vault",
                AnalyticsPendingPseudonymKeyVersion = 2,
                AnalyticsPseudonymKeyCreatedAt = Now
            },
            new MonitoredApp
            {
                Id = 2,
                Name = "aged",
                ProjectId = 10,
                AnalyticsVaultName = "vault",
                AnalyticsPseudonymKeyCreatedAt = Now.AddDays(-90)
            },
            new MonitoredApp
            {
                Id = 3,
                Name = "fresh",
                ProjectId = 10,
                AnalyticsVaultName = "vault",
                AnalyticsPseudonymKeyCreatedAt = Now
            },
            new MonitoredApp
            {
                Id = 4,
                Name = "no-vault",
                ProjectId = 10,
                AnalyticsPseudonymKeyCreatedAt = Now.AddDays(-90)
            });
        await SaveAsync();

        var candidates = await _repository.GetAnalyticsKeyRotationCandidatesAsync(Now.AddDays(-30), 100, Ct);

        Assert.Equal(["pending", "aged"], candidates.Select(app => app.Name));
    }

    [Fact]
    public async Task GetAnalyticsKeyRotationCandidatesAsync_ClampsTheBatchSizeToAtLeastOne()
    {
        _db.MonitoredApps.AddRange(
            new MonitoredApp
            {
                Id = 1,
                Name = "first",
                ProjectId = 10,
                AnalyticsVaultName = "vault",
                AnalyticsPendingPseudonymKeyVersion = 2
            },
            new MonitoredApp
            {
                Id = 2,
                Name = "second",
                ProjectId = 10,
                AnalyticsVaultName = "vault",
                AnalyticsPendingPseudonymKeyVersion = 2
            });
        await SaveAsync();

        var candidates = await _repository.GetAnalyticsKeyRotationCandidatesAsync(Now, 0, Ct);

        Assert.Equal(["first"], candidates.Select(app => app.Name));
    }

    [Fact]
    public async Task GetAnalyticsConfiguredAppsPageAsync_WalksForwardFromTheCursor()
    {
        _db.MonitoredApps.AddRange(
            new MonitoredApp { Id = 1, Name = "one", ProjectId = 10, AnalyticsVaultName = "vault" },
            new MonitoredApp { Id = 2, Name = "two", ProjectId = 10, AnalyticsVaultName = "vault" },
            new MonitoredApp { Id = 3, Name = "three", ProjectId = 10, AnalyticsVaultName = "vault" },
            new MonitoredApp { Id = 4, Name = "no-vault", ProjectId = 10 });
        await SaveAsync();

        var page = await _repository.GetAnalyticsConfiguredAppsPageAsync(1, 2, Ct);

        Assert.Equal(["two", "three"], page.Select(app => app.Name));
        Assert.Empty(await _repository.GetAnalyticsConfiguredAppsPageAsync(3, 2, Ct));
    }

    public void Dispose() => _db.Dispose();
}
