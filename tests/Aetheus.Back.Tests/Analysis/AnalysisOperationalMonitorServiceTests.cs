// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Analysis;

public sealed class AnalysisOperationalMonitorServiceTests
{
    [Fact]
    public void DetectIssues_ReportsAllRequiredOperationalFailureFamilies()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new AnalysisOperationalSnapshot(
            [new AnalysisTrackingHealthRow(1, 10, "Failed", now.AddHours(-2).UtcDateTime, "provider unavailable")],
            [new AnalysisServerHealthRow(1, 20, "runner", """
                [
                  "scanner-cleanup:degraded:2 residual workspaces require operator review",
                  "scanner-db:trivy:degraded:database is 72 hours old",
                  "scanner:opengrep:1.22.0:unavailable:verified binary missing"
                ]
                """)],
            [new AnalysisStorageHealthRow(1, 10, 900, 1_800_000)]);
        var options = new AnalysisPlatformOptions
        {
            ProjectDailyReportLimit = 1000,
            ProjectDailyBytesLimit = 2_000_000,
            StorageWarningPercent = 80,
            ScannerManifestMaxAgeDays = 45
        };
        var manifest = new ScannerManifest { SchemaVersion = 1, UpdatedAt = now.AddDays(-60).UtcDateTime };

        var issues = AnalysisOperationalMonitorService.DetectIssues(snapshot, manifest, options, now);

        Assert.Contains(issues, issue => issue.EventType == "analysis.operational.scanner-obsolete");
        Assert.Contains(issues, issue => issue.EventType == "analysis.operational.cve-sync-failed");
        Assert.Contains(issues, issue => issue.EventType == "analysis.operational.cve-db-stale");
        Assert.Contains(issues, issue => issue.EventType == "analysis.operational.workspace-residual");
        Assert.Contains(issues, issue => issue.EventType == "analysis.operational.scanner-unavailable");
        Assert.Contains(issues, issue => issue.EventType == "analysis.operational.storage-drift");
    }

    /// <summary>
    /// The probe says "unavailable" both for a scanner that is broken and for one this host will never
    /// be able to run. Only the first is an operator's problem: a Windows or arm64 runner cannot grow
    /// x64 Linux support, so alerting on it repeats every cooldown with nothing to do, which is how an
    /// alert channel stops being read. The capability stays reported on the server either way.
    /// </summary>
    [Fact]
    public void DetectIssues_AScannerTheHostCanNeverRun_IsNotAnOperationalAlert()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new AnalysisOperationalSnapshot(
            [],
            [new AnalysisServerHealthRow(1, 20, "runner", """
                [
                  "scanner:opengrep:1.22.0:unavailable:verified binary supports Linux x64 only",
                  "scanner:trivy:0.60.0:unavailable:docker daemon or socket unavailable"
                ]
                """)],
            []);
        var options = new AnalysisPlatformOptions
        {
            ProjectDailyReportLimit = 1000,
            ProjectDailyBytesLimit = 2_000_000,
            StorageWarningPercent = 80,
            ScannerManifestMaxAgeDays = 45
        };
        var manifest = new ScannerManifest { SchemaVersion = 1, UpdatedAt = now.UtcDateTime };

        var issues = AnalysisOperationalMonitorService.DetectIssues(snapshot, manifest, options, now);

        // The broken one still alerts: this filters a platform fact, it does not silence the family.
        var alert = Assert.Single(issues, issue => issue.EventType == "analysis.operational.scanner-unavailable");
        Assert.Contains("docker daemon", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(issues, issue => issue.Message.Contains("Linux x64 only", StringComparison.Ordinal));
    }

    private const string DockerDownCapability = """["scanner:trivy:0.60.0:unavailable:docker daemon or socket unavailable"]""";
    private const string ScannerUnavailable = "analysis.operational.scanner-unavailable";

    private static AnalysisOperationalSnapshot SnapshotWith(string capabilitiesJson) =>
        new([], [new AnalysisServerHealthRow(1, 20, "runner", capabilitiesJson)], []);

    private static (AnalysisOperationalMonitorService Service, INotificationService Notifications, IAnalysisRepository Repository, FakeTimeProvider Clock) NewMonitor()
    {
        var notifications = Substitute.For<INotificationService>();
        var repository = Substitute.For<IAnalysisRepository>();
        var services = new ServiceCollection();
        services.AddSingleton(notifications);
        services.AddSingleton(repository);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 1, 7, 0, TimeSpan.Zero));
        var service = new AnalysisOperationalMonitorService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AnalysisPlatformOptions()),
            clock,
            NullLogger<AnalysisOperationalMonitorService>.Instance,
            leaderLease: LeaderLeaseDouble.Leader());
        return (service, notifications, repository, clock);
    }

    private static int ScannerAlerts(INotificationService notifications) =>
        notifications.ReceivedCalls().Count(call => call.GetArguments()[0] as string == ScannerUnavailable);

    /// <summary>R-1: the host reboots every night, Docker is down for a couple of minutes, and the cycle that runs when the
    /// backend comes back alerted (a warning in the system logs and a notification). A scanner seen unavailable must still
    /// be there at a later cycle, after the grace, to alert.</summary>
    [Fact]
    public async Task ARunCycle_DoesNotAlertAScannerSeenUnavailableForTheFirstTime_ItAlertsWhenALaterCycleStillFindsIt()
    {
        var (service, notifications, repository, clock) = NewMonitor();
        repository.GetOperationalSnapshotAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(SnapshotWith(DockerDownCapability));

        await service.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, ScannerAlerts(notifications));

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, ScannerAlerts(notifications));

        clock.Advance(TimeSpan.FromMinutes(60));
        await service.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, ScannerAlerts(notifications));
    }

    [Fact]
    public async Task ARunCycle_ForgetsAScannerThatCameBack_SoTheNextOutageIsGivenItsGraceAgain()
    {
        var (service, notifications, repository, clock) = NewMonitor();
        repository.GetOperationalSnapshotAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(SnapshotWith(DockerDownCapability), SnapshotWith("""["scanner:trivy:0.60.0:ready"]"""), SnapshotWith(DockerDownCapability));

        await service.RunCycleAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(60));
        await service.RunCycleAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(60));
        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(0, ScannerAlerts(notifications));
    }

    [Fact]
    public void DetectIssues_HealthySnapshotProducesNoAlert()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new AnalysisOperationalSnapshot(
            [new AnalysisTrackingHealthRow(1, 10, "Synchronized", now.AddMinutes(-10).UtcDateTime, null)],
            [new AnalysisServerHealthRow(1, 20, "runner", """
                ["scanner-cleanup:ready:0 residual workspaces", "scanner-db:trivy:ready:updated=2026-07-22T06:00:00Z"]
                """)],
            [new AnalysisStorageHealthRow(1, 10, 10, 10_000)]);
        var manifest = new ScannerManifest { SchemaVersion = 1, UpdatedAt = now.AddDays(-1).UtcDateTime };

        var issues = AnalysisOperationalMonitorService.DetectIssues(
            snapshot, manifest, new AnalysisPlatformOptions(), now);

        Assert.Empty(issues);
    }
}
