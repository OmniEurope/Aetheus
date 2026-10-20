// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>The Dependency-Track continuous synchronization: one cycle, its failure handling, and its schedule.</summary>
public sealed class AnalysisContinuousSyncServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly IAnalysisRepository _repo = Substitute.For<IAnalysisRepository>();
    private readonly IDependencyTrackClient _client = Substitute.For<IDependencyTrackClient>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly RecordingLogger<AnalysisContinuousSyncService> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AnalysisContinuousSyncService Sut(bool enabled = true, int intervalMinutes = 30, IPostgresLeaderLease? lease = null) => new(
        BackgroundServiceTestKit.ScopeFactory(
            (typeof(IAnalysisRepository), _repo), (typeof(IDependencyTrackClient), _client), (typeof(INotificationService), _notifications)),
        Options.Create(new DependencyTrackOptions { Enabled = enabled, SyncIntervalMinutes = intervalMinutes }),
        _clock, _logger, lease);

    private void Tracking(params AnalysisTrackingProject[] projects) =>
        _repo.GetActiveTrackingProjectsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(projects.ToList());

    private static AnalysisTrackingProject Project(int projectId, string externalId) => new()
    {
        OrganizationId = 1,
        ProjectId = projectId,
        ExternalProjectId = externalId,
        LastSbomReportId = projectId * 100
    };

    private static DependencyTrackVulnerability Vulnerability(string id) =>
        new(id, "left-pad", "1.0.0", "pkg:npm/left-pad@1.0.0", AnalysisSeverity.High, "Open");

    private void Vulnerabilities(string externalId, params DependencyTrackVulnerability[] found) =>
        _client.GetVulnerabilitiesAsync(externalId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DependencyTrackVulnerability>)found);

    [Fact]
    public async Task Cycle_ReadsUpToAHundredTrackedProjects()
    {
        Tracking();

        await Sut().RunCycleAsync(Ct);

        await _repo.Received(1).GetActiveTrackingProjectsAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cycle_SavesASucceededSnapshot_WithTheObservationsOfTheClockInstant()
    {
        var project = Project(5, "dt-5");
        Tracking(project);
        Vulnerabilities("dt-5", Vulnerability("CVE-2026-1"), Vulnerability("CVE-2026-2"));
        IReadOnlyCollection<AnalysisVulnerabilityObservation>? saved = null;
        await _repo.SaveTrackingSnapshotAsync(project, Arg.Do<IReadOnlyCollection<AnalysisVulnerabilityObservation>>(o => saved = o), Arg.Any<CancellationToken>());

        await Sut().RunCycleAsync(Ct);

        Assert.Equal("Succeeded", project.SyncStatus);
        Assert.Null(project.LastError);
        Assert.Equal(2, project.LastKnownVulnerabilityCount);
        Assert.Equal(Start.UtcDateTime, project.LastSyncAt);
        Assert.NotNull(saved);
        Assert.Equal(["CVE-2026-1", "CVE-2026-2"], saved.Select(o => o.VulnerabilityId).Order());
        Assert.All(saved, observation =>
        {
            Assert.True(observation.IsContinuous);
            Assert.Equal(Start.UtcDateTime, observation.ObservedAt);
            Assert.Equal(500, observation.AnalysisReportId);
        });
        await _notifications.DidNotReceiveWithAnyArgs().SendEventAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Cycle_RecordsNoNewObservation_WhenTheSnapshotDidNotChange()
    {
        var project = Project(5, "dt-5");
        Tracking(project);
        Vulnerabilities("dt-5", Vulnerability("CVE-2026-1"));
        await Sut().RunCycleAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(30));
        IReadOnlyCollection<AnalysisVulnerabilityObservation>? second = null;
        await _repo.SaveTrackingSnapshotAsync(project, Arg.Do<IReadOnlyCollection<AnalysisVulnerabilityObservation>>(o => second = o), Arg.Any<CancellationToken>());

        await Sut().RunCycleAsync(Ct);

        Assert.NotNull(second);
        Assert.Empty(second);
        Assert.Equal(Start.UtcDateTime.AddMinutes(30), project.LastSyncAt);
    }

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task Cycle_ATransientFailure_MarksTheProjectFailed_Notifies_AndTheNextProjectStillSyncs(Exception failure)
    {
        var broken = Project(1, "dt-1");
        var healthy = Project(2, "dt-2");
        Tracking(broken, healthy);
        _client.GetVulnerabilitiesAsync("dt-1", Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        Vulnerabilities("dt-2", Vulnerability("CVE-2026-9"));

        await Sut().RunCycleAsync(Ct);

        Assert.Equal("Failed", broken.SyncStatus);
        Assert.Equal(failure.Message, broken.LastError);
        Assert.Equal(Start.UtcDateTime, broken.LastSyncAt);
        await _repo.Received(1).SaveTrackingSnapshotAsync(broken, Arg.Is<IReadOnlyCollection<AnalysisVulnerabilityObservation>>(o => o.Count == 0), Arg.Any<CancellationToken>());
        await _notifications.Received(1).SendEventAsync("analysis.cve-sync.failed", Arg.Any<object>(), Arg.Any<CancellationToken>());
        Assert.Equal("Succeeded", healthy.SyncStatus);
        var error = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error);
        Assert.Contains("1", error.Message);
        Assert.Same(failure, error.Exception);
    }

    public static TheoryData<Exception> TransientFailures() => new()
    {
        new HttpRequestException("dependency-track returned 503"),
        new JsonException("unexpected token"),
        new TaskCanceledException("request timed out")
    };

    [Fact]
    public async Task Cycle_TruncatesALongFailureMessage_ToTwoThousandCharacters()
    {
        var project = Project(1, "dt-1");
        Tracking(project);
        _client.GetVulnerabilitiesAsync("dt-1", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException(new string('x', 5000)));

        await Sut().RunCycleAsync(Ct);

        Assert.Equal(2000, project.LastError!.Length);
    }

    [Fact]
    public async Task Cycle_WhenShuttingDown_ACancelledRequestIsNotRecordedAsASyncFailure()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var project = Project(1, "dt-1");
        Tracking(project);
        _client.GetVulnerabilitiesAsync("dt-1", Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<DependencyTrackVulnerability>>>(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("stopping", null, cts.Token);
        });

        await Assert.ThrowsAsync<TaskCanceledException>(() => Sut().RunCycleAsync(cts.Token));

        Assert.NotEqual("Failed", project.SyncStatus);
        await _repo.DidNotReceiveWithAnyArgs().SaveTrackingSnapshotAsync(default!, default!, Ct);
        await _notifications.DidNotReceiveWithAnyArgs().SendEventAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Cycle_AnUnexpectedException_MarksTheProjectFailed_Notifies_AndTheNextProjectStillSyncs()
    {
        var broken = Project(1, "dt-1");
        var healthy = Project(2, "dt-2");
        Tracking(broken, healthy);
        var failure = new InvalidOperationException("unexpected state");
        _client.GetVulnerabilitiesAsync("dt-1", Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        Vulnerabilities("dt-2", Vulnerability("CVE-2026-9"));

        await Sut().RunCycleAsync(Ct);

        Assert.Equal("Failed", broken.SyncStatus);
        Assert.Equal("unexpected state", broken.LastError);
        await _notifications.Received(1).SendEventAsync("analysis.cve-sync.failed", Arg.Any<object>(), Arg.Any<CancellationToken>());
        Assert.Equal("Succeeded", healthy.SyncStatus);
        Assert.Same(failure, Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error).Exception);
    }

    [Fact]
    public async Task Cycle_WhenShuttingDown_AnUnexpectedExceptionIsNotRecordedAsASyncFailure()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var project = Project(1, "dt-1");
        Tracking(project);
        _client.GetVulnerabilitiesAsync("dt-1", Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<DependencyTrackVulnerability>>>(_ =>
        {
            cts.Cancel();
            throw new InvalidOperationException("torn down");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().RunCycleAsync(cts.Token));

        Assert.NotEqual("Failed", project.SyncStatus);
        await _repo.DidNotReceiveWithAnyArgs().SaveTrackingSnapshotAsync(default!, default!, Ct);
        await _notifications.DidNotReceiveWithAnyArgs().SendEventAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Execute_WhenDisabled_DoesNothing_NotEvenTakingTheLease()
    {
        var (lease, leaseName) = BackgroundServiceTestKit.NonLeaderLease();
        var sut = Sut(enabled: false, lease: lease);

        await sut.StartAsync(Ct);
        _clock.Advance(TimeSpan.FromHours(2));
        await sut.StopAsync(Ct);

        Assert.False(leaseName.IsCompleted);
        await _repo.DidNotReceiveWithAnyArgs().GetActiveTrackingProjectsAsync(default, Ct);
    }

    [Fact]
    public async Task Execute_WhenEnabled_RunsUnderTheContinuousSyncLease_AndNothingWhenNotLeader()
    {
        var (lease, leaseName) = BackgroundServiceTestKit.NonLeaderLease();
        var sut = Sut(lease: lease);

        await sut.StartAsync(Ct);
        Assert.Equal("aetheus:analysis-continuous-sync", await leaseName.NameAsync(Ct));
        _clock.Advance(TimeSpan.FromHours(2));
        await sut.StopAsync(Ct);

        await _repo.DidNotReceiveWithAnyArgs().GetActiveTrackingProjectsAsync(default, Ct);
    }

    [Theory]
    [InlineData(1, 5)]      // below the floor: clamped up to five minutes
    [InlineData(30, 30)]
    [InlineData(5000, 1440)] // above the ceiling: clamped down to a day
    public async Task Loop_SyncsAtStartup_ThenEveryClampedInterval(int configuredMinutes, int effectiveMinutes)
    {
        var cycles = new SemaphoreSlim(0);
        var calls = 0;
        _repo.GetActiveTrackingProjectsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref calls);
            cycles.Release();
            return Task.FromResult(new List<AnalysisTrackingProject>());
        });
        var sut = Sut(intervalMinutes: configuredMinutes);

        await sut.StartAsync(Ct);
        await cycles.AwaitAsync(Ct);
        Assert.Equal(1, calls);

        _clock.Advance(TimeSpan.FromMinutes(effectiveMinutes - 1));
        Assert.Equal(1, calls);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await cycles.AwaitAsync(Ct);
        Assert.Equal(2, calls);

        await sut.StopAsync(Ct);
        Assert.Empty(_logger.Records);
    }

    [Fact]
    public async Task Loop_AFailedCycle_IsLogged_AndTheNextCycleRuns()
    {
        var cycles = new SemaphoreSlim(0);
        var calls = 0;
        _repo.GetActiveTrackingProjectsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            cycles.Release();
            return call == 1 ? throw new InvalidOperationException("database down") : Task.FromResult(new List<AnalysisTrackingProject>());
        });
        var sut = Sut();

        await sut.StartAsync(Ct);
        await cycles.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(30));
        await cycles.AwaitAsync(Ct);
        await sut.StopAsync(Ct);

        Assert.Equal(2, calls);
        var error = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error);
        Assert.Contains("cycle failed", error.Message);
    }
}
