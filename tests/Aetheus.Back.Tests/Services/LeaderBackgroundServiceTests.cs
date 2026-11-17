// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Services;

/// <summary>The shared "run this work only while leader" base class (ADR-029): the leader and the not-leader branches,
/// the token the loop receives, and the lease name that is required.</summary>
public sealed class LeaderBackgroundServiceTests
{
    [Fact]
    public async Task WhileLeader_TheLoopRunsOnceUnderTheLeaseName()
    {
        var lease = LeaderLeaseDouble.Leader();
        var worker = new RecordingWorker(lease, "aetheus:test-worker");

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.ExecuteTask!.WaitAsync(BackgroundServiceTestKit.Bound, TestContext.Current.CancellationToken);

        Assert.Equal(1, worker.LoopRuns);
        Assert.Equal(1, lease.LeaderRequests);
        Assert.Equal("aetheus:test-worker", lease.LastLeaseName);
    }

    [Fact]
    public async Task WhileNotLeader_TheLoopNeverRuns()
    {
        var lease = LeaderLeaseDouble.NotLeader();
        var worker = new RecordingWorker(lease, "aetheus:test-worker");

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.ExecuteTask!.WaitAsync(BackgroundServiceTestKit.Bound, TestContext.Current.CancellationToken);

        Assert.Equal(0, worker.LoopRuns);
        Assert.Equal(1, lease.LeaderRequests);
    }

    [Fact]
    public async Task WhenLeadershipIsLostMidLoop_TheLoopReceivesTheCancellationTheLeaseRaises()
    {
        // The lease hands the loop its own token (cancelled when the session is lost or the instance goes to
        // reserve), not the host's stopping token: the loop must end when that token is cancelled.
        using var leaseLost = new CancellationTokenSource();
        var lease = Substitute.For<IPostgresLeaderLease>();
        lease.RunAsLeaderAsync(Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(leaseLost.Token));
        var worker = new RecordingWorker(lease, "aetheus:test-worker") { WaitForCancellation = true };

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.Entered.WaitAsync(BackgroundServiceTestKit.Bound, TestContext.Current.CancellationToken);
        Assert.False(worker.ExecuteTask!.IsCompleted);

        await leaseLost.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.ExecuteTask!.WaitAsync(BackgroundServiceTestKit.Bound, TestContext.Current.CancellationToken));
        Assert.True(worker.SawCancellation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ALeaseNameIsRequired(string leaseName)
    {
        Assert.Throws<ArgumentException>(() => new RecordingWorker(LeaderLeaseDouble.Leader(), leaseName));
    }

    private sealed class RecordingWorker(IPostgresLeaderLease lease, string leaseName) : LeaderBackgroundService(lease, leaseName)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loopRuns;

        public int LoopRuns => Volatile.Read(ref _loopRuns);

        public bool WaitForCancellation { get; init; }

        public bool SawCancellation { get; private set; }

        public Task Entered => _entered.Task;

        protected override async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref _loopRuns);
            _entered.TrySetResult();
            if (!WaitForCancellation) return;
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }
        }
    }
}
