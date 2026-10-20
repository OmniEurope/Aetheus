// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.ExternalRepos;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>Which external-repo mirrors the scheduled sweep fetches, by the injected clock, and how it survives errors.</summary>
public sealed class ExternalRepoSyncServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly IGitLightRepository _light = Substitute.For<IGitLightRepository>();
    private readonly IGitRepository _git = Substitute.For<IGitRepository>();
    private readonly IExternalRepoMirrorService _mirror = Substitute.For<IExternalRepoMirrorService>();
    private readonly IEntityChangeNotifier _notifier = Substitute.For<IEntityChangeNotifier>();
    private readonly RecordingLogger<ExternalRepoSyncService> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ExternalRepoSyncService Sut(IPostgresLeaderLease? lease = null) => new(
        BackgroundServiceTestKit.ScopeFactory(
            (typeof(IGitLightRepository), _light), (typeof(IGitRepository), _git),
            (typeof(IExternalRepoMirrorService), _mirror), (typeof(IEntityChangeNotifier), _notifier)),
        _clock, _logger, lease);

    private static GitInternalRepo Mirror(int id, int? connectionId) => new() { Id = id, ProjectId = 100 + id, GitConnectionId = connectionId };

    /// <summary>Registers a mirror repo and its connection; returns both.</summary>
    private (GitInternalRepo Repo, GitConnection Connection) Register(
        int id, DateTime? lastFetchedAt, int intervalMinutes = 15, bool autoSync = true,
        GitMirrorStatus status = GitMirrorStatus.Ready)
    {
        var connection = new GitConnection
        {
            Id = id * 10,
            ProjectId = 100 + id,
            AutoSyncEnabled = autoSync,
            MirrorStatus = status,
            FetchIntervalMinutes = intervalMinutes,
            LastFetchedAt = lastFetchedAt
        };
        var repo = Mirror(id, connection.Id);
        _git.FindConnectionAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        return (repo, connection);
    }

    private void Mirrors(params GitInternalRepo[] repos) => _light.GetAllAsync(Arg.Any<CancellationToken>()).Returns(repos.ToList());

    [Fact]
    public async Task Sweep_SyncsADueMirror_SavesBothRepositories_AndBroadcastsTheProject()
    {
        var (repo, connection) = Register(1, lastFetchedAt: Start.UtcDateTime.AddMinutes(-20));
        Mirrors(repo);

        await Sut().SyncDueMirrorsAsync(Ct);

        await _mirror.Received(1).SyncAsync(connection, repo, Arg.Any<CancellationToken>());
        await _git.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _light.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(ResourceType.Project, 101, EntityChangeOps.Updated, Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task Sweep_SyncsAMirrorThatWasNeverFetched()
    {
        var (repo, connection) = Register(1, lastFetchedAt: null);
        Mirrors(repo);

        await Sut().SyncDueMirrorsAsync(Ct);

        await _mirror.Received(1).SyncAsync(connection, repo, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(14, 59, false)]   // 14m59s since the last fetch, interval 15m: not yet
    [InlineData(15, 0, true)]     // exactly the interval: due
    [InlineData(16, 0, true)]
    public async Task Sweep_UsesTheConnectionIntervalAndTheInjectedClock(int minutes, int seconds, bool due)
    {
        var (repo, _) = Register(1, lastFetchedAt: Start.UtcDateTime - new TimeSpan(0, minutes, seconds), intervalMinutes: 15);
        Mirrors(repo);

        await Sut().SyncDueMirrorsAsync(Ct);

        await _mirror.Received(due ? 1 : 0).SyncAsync(Arg.Any<GitConnection>(), Arg.Any<GitInternalRepo>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 1)]        // a zero or negative interval is clamped up to one minute
    [InlineData(-5, 1)]
    [InlineData(100000, 1440)] // an absurd interval is clamped down to one day
    public async Task Sweep_ClampsTheFetchInterval(int configuredMinutes, int effectiveMinutes)
    {
        var justBefore = Register(1, Start.UtcDateTime.AddMinutes(-(effectiveMinutes - 1)).AddSeconds(-59), configuredMinutes);
        var atInterval = Register(2, Start.UtcDateTime.AddMinutes(-effectiveMinutes), configuredMinutes);
        Mirrors(justBefore.Repo, atInterval.Repo);

        await Sut().SyncDueMirrorsAsync(Ct);

        await _mirror.DidNotReceive().SyncAsync(justBefore.Connection, justBefore.Repo, Arg.Any<CancellationToken>());
        await _mirror.Received(1).SyncAsync(atInterval.Connection, atInterval.Repo, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_SkipsMirrorsThatAreNotEligible()
    {
        var old = Start.UtcDateTime.AddDays(-1);
        var noConnectionId = Mirror(1, connectionId: null);
        var missingConnection = Mirror(2, connectionId: 999);
        _git.FindConnectionAsync(999, Arg.Any<CancellationToken>()).Returns((GitConnection?)null);
        var autoSyncOff = Register(3, old, autoSync: false);
        var alreadySyncing = Register(4, old, status: GitMirrorStatus.Syncing);
        Mirrors(noConnectionId, missingConnection, autoSyncOff.Repo, alreadySyncing.Repo);

        await Sut().SyncDueMirrorsAsync(Ct);

        await _mirror.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, Ct);
        await _git.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
        await _notifier.DidNotReceiveWithAnyArgs().BroadcastAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Sweep_OneFailingMirror_DoesNotStopTheOthers_AndIsLoggedWithItsRepository()
    {
        var broken = Register(1, null);
        var healthy = Register(2, null);
        Mirrors(broken.Repo, healthy.Repo);
        _mirror.SyncAsync(broken.Connection, broken.Repo, Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));

        await Sut().SyncDueMirrorsAsync(Ct);

        await _mirror.Received(1).SyncAsync(healthy.Connection, healthy.Repo, Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(ResourceType.Project, 102, EntityChangeOps.Updated, Arg.Any<CancellationToken>(), Arg.Any<int?>());
        await _notifier.DidNotReceive().BroadcastAsync(ResourceType.Project, 101, Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
        var error = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error);
        Assert.Contains("1", error.Message);
        Assert.IsType<IOException>(error.Exception);
    }

    [Fact]
    public async Task Sweep_Cancellation_Propagates_AndIsNotLoggedAsAMirrorFailure()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = Register(1, null);
        var second = Register(2, null);
        Mirrors(first.Repo, second.Repo);
        _mirror.SyncAsync(Arg.Any<GitConnection>(), Arg.Any<GitInternalRepo>(), Arg.Any<CancellationToken>())
            .Returns<Task<GitMirrorStatus>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => Sut().SyncDueMirrorsAsync(cts.Token));

        await _mirror.ReceivedWithAnyArgs(1).SyncAsync(default!, default!, Ct);
        Assert.Empty(_logger.Records);
    }

    [Fact]
    public async Task Loop_SweepsEveryMinute_LogsAFailedSweep_AndKeepsGoing()
    {
        var sweeps = new SemaphoreSlim(0);
        var calls = 0;
        _light.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            sweeps.Release();
            return call == 1 ? throw new InvalidOperationException("database down") : Task.FromResult(new List<GitInternalRepo>());
        });
        var (lease, started) = BackgroundServiceTestKit.LeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        await started.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await sweeps.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await sweeps.AwaitAsync(Ct);
        await sut.StopAsync(Ct);

        Assert.Equal(2, calls);
        var error = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error);
        Assert.Contains("sweep failed", error.Message);
    }

    [Fact]
    public async Task WithALease_RunsUnderTheExternalRepoSyncLease_AndNothingWhenNotLeader()
    {
        var (lease, leaseName) = BackgroundServiceTestKit.NonLeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        Assert.Equal("aetheus:external-repo-sync", await leaseName.NameAsync(Ct));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await sut.StopAsync(Ct);

        await _light.DidNotReceiveWithAnyArgs().GetAllAsync(Ct);
    }
}
