// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Backups;

/// <summary>What the backup scheduler dispatches on a tick, by the injected clock, and how it survives errors.</summary>
public sealed class BackupSchedulerServiceTests
{
    // 12:00:30 UTC: the "0 12 * * *" occurrence of 12:00:00 is inside the 2-minute window.
    private static readonly DateTimeOffset Noon = new(2026, 7, 9, 12, 0, 30, TimeSpan.Zero);
    private const string DailyAtNoon = "0 12 * * *";

    private readonly FakeTimeProvider _clock = new(Noon);
    private readonly IBackupRepository _repo = Substitute.For<IBackupRepository>();
    private readonly IBackupPolicyService _service = Substitute.For<IBackupPolicyService>();
    private readonly RecordingLogger<BackupSchedulerService> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private BackupSchedulerService Sut(IPostgresLeaderLease? lease = null) => new(
        BackgroundServiceTestKit.ScopeFactory((typeof(IBackupRepository), _repo), (typeof(IBackupPolicyService), _service)),
        _clock, _logger, lease);

    private void Policies(params BackupPolicy[] policies) =>
        _repo.GetEnabledPoliciesAsync(Arg.Any<CancellationToken>()).Returns(policies.ToList());

    private static BackupPolicy Policy(int id, string schedule = DailyAtNoon, string? restoreCheck = null,
        DateTime? lastRun = null, DateTime? lastRestoreCheck = null) => new()
        {
            Id = id,
            ScheduleCron = schedule,
            RestoreCheckCron = restoreCheck,
            LastRunAt = lastRun,
            LastRestoreCheckAt = lastRestoreCheck
        };

    [Fact]
    public async Task Tick_DispatchesABackup_ForAPolicyWhoseCronFiresInTheWindow()
    {
        var due = Policy(1);
        var notDue = Policy(2, schedule: "0 13 * * *");
        Policies(due, notDue);

        await Sut().TickAsync(Ct);

        await _service.Received(1).TriggerBackupAsync(due, Arg.Any<CancellationToken>());
        await _service.DidNotReceive().TriggerBackupAsync(notDue, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tick_DoesNotDispatchTwice_WhenThePolicyAlreadyRanInsideTheWindow()
    {
        Policies(Policy(1, lastRun: Noon.UtcDateTime.AddSeconds(-45)));

        await Sut().TickAsync(Ct);

        await _service.DidNotReceiveWithAnyArgs().TriggerBackupAsync(default!, Ct);
    }

    [Fact]
    public async Task Tick_UsesTheInjectedClock_ForTheTwoMinuteWindow()
    {
        var policy = Policy(1);
        Policies(policy);

        _clock.SetUtcNow(new DateTimeOffset(2026, 7, 9, 12, 1, 59, TimeSpan.Zero));
        await Sut().TickAsync(Ct);
        await _service.Received(1).TriggerBackupAsync(policy, Arg.Any<CancellationToken>());

        _service.ClearReceivedCalls();
        _clock.SetUtcNow(new DateTimeOffset(2026, 7, 9, 12, 2, 1, TimeSpan.Zero));
        await Sut().TickAsync(Ct);
        await _service.DidNotReceiveWithAnyArgs().TriggerBackupAsync(default!, Ct);
    }

    [Fact]
    public async Task Tick_DispatchesARestoreCheck_OfTheLatestSuccessfulRun()
    {
        var policy = Policy(3, schedule: "0 13 * * *", restoreCheck: DailyAtNoon);
        var latest = new BackupRun { Id = 77, BackupPolicyId = 3 };
        Policies(policy);
        _repo.GetLatestSuccessfulRunAsync(3, Arg.Any<CancellationToken>()).Returns(latest);

        await Sut().TickAsync(Ct);

        await _service.Received(1).TriggerRestoreCheckAsync(latest, policy, Arg.Any<CancellationToken>());
        await _service.DidNotReceiveWithAnyArgs().TriggerBackupAsync(default!, Ct);
    }

    [Fact]
    public async Task Tick_SkipsTheRestoreCheck_WhenThePolicyHasNoSuccessfulRun()
    {
        Policies(Policy(3, schedule: "0 13 * * *", restoreCheck: DailyAtNoon));
        _repo.GetLatestSuccessfulRunAsync(3, Arg.Any<CancellationToken>()).Returns((BackupRun?)null);

        await Sut().TickAsync(Ct);

        await _service.DidNotReceiveWithAnyArgs().TriggerRestoreCheckAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Tick_NeverChecksRestore_WhenNoRestoreCronIsSet_OrItAlreadyRan()
    {
        Policies(
            Policy(1, schedule: "0 13 * * *", restoreCheck: null),
            Policy(2, schedule: "0 13 * * *", restoreCheck: DailyAtNoon, lastRestoreCheck: Noon.UtcDateTime.AddSeconds(-30)));

        await Sut().TickAsync(Ct);

        await _repo.DidNotReceiveWithAnyArgs().GetLatestSuccessfulRunAsync(default, Ct);
        await _service.DidNotReceiveWithAnyArgs().TriggerRestoreCheckAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Tick_DispatchesBackupAndRestoreCheck_WhenBothAreDue()
    {
        var policy = Policy(4, restoreCheck: DailyAtNoon);
        var latest = new BackupRun { Id = 5 };
        Policies(policy);
        _repo.GetLatestSuccessfulRunAsync(4, Arg.Any<CancellationToken>()).Returns(latest);

        await Sut().TickAsync(Ct);

        await _service.Received(1).TriggerBackupAsync(policy, Arg.Any<CancellationToken>());
        await _service.Received(1).TriggerRestoreCheckAsync(latest, policy, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tick_IgnoresAnInvalidCron_AndStillRunsTheOthers()
    {
        var good = Policy(2);
        Policies(Policy(1, schedule: "not a cron"), good);

        await Sut().TickAsync(Ct);

        await _service.Received(1).TriggerBackupAsync(good, Arg.Any<CancellationToken>());
        await _service.ReceivedWithAnyArgs(1).TriggerBackupAsync(default!, Ct);
    }

    [Fact]
    public async Task Tick_OneFailingPolicy_DoesNotStopTheOthers_AndIsLogged()
    {
        var broken = Policy(1);
        var healthy = Policy(2);
        Policies(broken, healthy);
        _service.TriggerBackupAsync(broken, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("agent offline"));

        await Sut().TickAsync(Ct);

        await _service.Received(1).TriggerBackupAsync(healthy, Arg.Any<CancellationToken>());
        var warning = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("1", warning.Message);
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    [Fact]
    public async Task Tick_Cancellation_Propagates_AndIsNotLoggedAsAPolicyFailure()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Policies(Policy(1), Policy(2));
        _service.TriggerBackupAsync(Arg.Any<BackupPolicy>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => Sut().TickAsync(cts.Token));

        await _service.ReceivedWithAnyArgs(1).TriggerBackupAsync(default!, Ct);
        Assert.Empty(_logger.Records);
    }

    [Fact]
    public async Task Loop_RunsATickEveryMinute_LogsAFailedTick_AndKeepsGoing()
    {
        var ticks = new SemaphoreSlim(0);
        var calls = 0;
        _repo.GetEnabledPoliciesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            ticks.Release();
            return call == 1 ? throw new InvalidOperationException("database down") : Task.FromResult(new List<BackupPolicy>());
        });
        var (lease, started) = BackgroundServiceTestKit.LeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        await started.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ticks.AwaitAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ticks.AwaitAsync(Ct);
        await sut.StopAsync(Ct);

        Assert.Equal(2, calls);
        var warning = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("tick failed", warning.Message);
    }

    [Fact]
    public async Task Loop_StopsOnCancellation_WithoutAnErrorLog()
    {
        var sut = Sut();

        await sut.StartAsync(Ct);
        await sut.StopAsync(Ct);

        Assert.Empty(_logger.Records);
        await _repo.DidNotReceiveWithAnyArgs().GetEnabledPoliciesAsync(Ct);
    }

    [Fact]
    public async Task WithALease_RunsUnderTheBackupSchedulerLease_AndNothingWhenNotLeader()
    {
        var (lease, leaseName) = BackgroundServiceTestKit.NonLeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        Assert.Equal("aetheus:backup-scheduler", await leaseName.NameAsync(Ct));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await sut.StopAsync(Ct);

        await _repo.DidNotReceiveWithAnyArgs().GetEnabledPoliciesAsync(Ct);
    }
}
