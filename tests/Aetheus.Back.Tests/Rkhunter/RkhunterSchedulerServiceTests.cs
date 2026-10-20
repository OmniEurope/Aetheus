// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Rkhunter;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>When the rkhunter scheduler queues a scan (cron window, de-dupe) and how it survives errors.</summary>
public sealed class RkhunterSchedulerServiceTests
{
    // 03:00:30 UTC: the "0 3 * * *" occurrence of 03:00:00 is within the last minute.
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 3, 0, 30, TimeSpan.Zero);
    private const string DailyAtThree = "0 3 * * *";

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly IRkhunterRepository _repo = Substitute.For<IRkhunterRepository>();
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly RecordingLogger<RkhunterSchedulerService> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RkhunterSchedulerService Sut(IPostgresLeaderLease? lease = null, TimeProvider? clock = null) => new(
        BackgroundServiceTestKit.ScopeFactory((typeof(IRkhunterRepository), _repo), (typeof(ITaskService), _tasks)),
        _logger, clock ?? _clock, lease);

    private void Scheduled(params RkhunterState[] states) =>
        _repo.GetScheduledStatesAsync(Arg.Any<CancellationToken>()).Returns(states.ToList());

    private static RkhunterState State(int serverId, string? cron = DailyAtThree, DateTime? lastScan = null) =>
        new() { ServerId = serverId, ScanScheduleCron = cron, LastScheduledScanAt = lastScan };

    [Fact]
    public async Task Check_QueuesATypedScanTask_NotifiesAndStamps_WhenTheCronFiredWithinTheLastMinute()
    {
        Scheduled(State(7));
        ServerTask? queued = null;
        await _repo.AddTaskAsync(Arg.Do<ServerTask>(task => queued = task), Arg.Any<CancellationToken>());

        await Sut().CheckScheduledScansAsync(Ct);

        Assert.NotNull(queued);
        Assert.Equal(7, queued.ServerId);
        Assert.Equal(OperationKind.RkhunterScan, queued.Operation);
        Assert.Equal(ExecutorType.Operation, queued.Executor);
        Assert.Equal(300, queued.TimeoutSeconds);
        await _tasks.Received(1).NotifyTaskQueuedAsync(queued, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdateLastScheduledScanAsync(7, Arg.Any<CancellationToken>());
        Assert.Contains(_logger.Records, entry => entry.Level == LogLevel.Information && entry.Message.Contains('7'));
    }

    [Theory]
    [InlineData("2026-10-13T03:00:00Z", true)]   // exactly on the occurrence
    [InlineData("2026-10-13T03:00:59Z", true)]   // last second of the window
    [InlineData("2026-10-13T03:01:01Z", false)]  // the occurrence is more than a minute old (missed)
    [InlineData("2026-10-13T02:59:59Z", false)]  // not fired yet
    public async Task Check_FiresOnlyInsideTheOneMinuteWindow_OfTheInjectedClock(string now, bool fires)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        Scheduled(State(7));

        await Sut(clock: clock).CheckScheduledScansAsync(Ct);

        await _repo.Received(fires ? 1 : 0).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
        await _repo.Received(fires ? 1 : 0).UpdateLastScheduledScanAsync(7, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(60, false)]   // scanned a minute ago: the same occurrence, not repeated
    [InlineData(119, false)]  // still inside the two-minute guard
    [InlineData(120, true)]   // the guard is over
    public async Task Check_DoesNotRescan_InsideTheTwoMinuteGuard(int secondsSinceLastScan, bool fires)
    {
        Scheduled(State(7, lastScan: Start.UtcDateTime.AddSeconds(-secondsSinceLastScan)));

        await Sut().CheckScheduledScansAsync(Ct);

        await _repo.Received(fires ? 1 : 0).AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_QueuesOneScanPerDueServer_AndSkipsTheOthers()
    {
        Scheduled(State(1), State(2, cron: "0 4 * * *"), State(3));

        await Sut().CheckScheduledScansAsync(Ct);

        await _repo.Received(1).UpdateLastScheduledScanAsync(1, Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdateLastScheduledScanAsync(3, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateLastScheduledScanAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_AnInvalidCron_IsLoggedWithTheServer_AndTheOthersStillRun()
    {
        Scheduled(State(1, cron: "every tuesday"), State(2));

        await Sut().CheckScheduledScansAsync(Ct);

        await _repo.Received(1).UpdateLastScheduledScanAsync(2, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().UpdateLastScheduledScanAsync(1, Arg.Any<CancellationToken>());
        var error = Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error);
        Assert.Contains("1", error.Message);
        Assert.NotNull(error.Exception);
    }

    [Fact]
    public async Task Check_AFailingServer_DoesNotStopTheOthers_AndIsNotStamped()
    {
        Scheduled(State(1), State(2));
        _repo.AddTaskAsync(Arg.Is<ServerTask>(task => task.ServerId == 1), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database down"));

        await Sut().CheckScheduledScansAsync(Ct);

        await _repo.DidNotReceive().UpdateLastScheduledScanAsync(1, Arg.Any<CancellationToken>());
        await _repo.Received(1).UpdateLastScheduledScanAsync(2, Arg.Any<CancellationToken>());
        Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Check_WhenTheScheduledStatesCannotBeRead_LogsAndDoesNotThrow()
    {
        _repo.GetScheduledStatesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database down"));

        await Sut().CheckScheduledScansAsync(Ct);

        Assert.Single(_logger.Records, entry => entry.Level == LogLevel.Error);
        await _repo.DidNotReceiveWithAnyArgs().AddTaskAsync(default!, Ct);
    }

    [Fact]
    public async Task Loop_ChecksAtStartup_ThenEveryMinute_AndStopsOnCancellation()
    {
        var checks = new SemaphoreSlim(0);
        var calls = 0;
        _repo.GetScheduledStatesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref calls);
            checks.Release();
            return Task.FromResult(new List<RkhunterState>());
        });
        var sut = Sut();

        await sut.StartAsync(Ct);
        await checks.AwaitAsync(Ct);
        Assert.Equal(1, calls);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await checks.AwaitAsync(Ct);
        Assert.Equal(2, calls);

        await sut.StopAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, calls);
        Assert.Empty(_logger.Records);
    }

    [Fact]
    public async Task WithALease_RunsUnderTheRkhunterLease_AndNothingWhenNotLeader()
    {
        var (lease, leaseName) = BackgroundServiceTestKit.NonLeaderLease();
        var sut = Sut(lease);

        await sut.StartAsync(Ct);
        Assert.Equal("aetheus:rkhunter-scheduler", await leaseName.NameAsync(Ct));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await sut.StopAsync(Ct);

        await _repo.DidNotReceiveWithAnyArgs().GetScheduledStatesAsync(Ct);
    }
}
