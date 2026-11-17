// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// ACORE-002: a cancelled function is acknowledged as stopped only once it really ended (or its
/// mutations were effectively cancelled). An executor that stops waiting for a function that is still
/// running would let a synchronous publication block keep mutating after the task was reported stopped.
/// </summary>
public sealed class PipelineFunctionStopProofTests
{
    private static readonly PipelineFunctionDescriptor Descriptor = new(
        "fake-publish", PipelineFunctionAccess.Build, "Test function.", [], []);

    private readonly List<(string Line, TaskLogLevel Level)> _log = [];

    private Task Capture(string line, TaskLogLevel level)
    {
        lock (_log)
            _log.Add((line, level));
        return Task.CompletedTask;
    }

    private static PipelineFunctionOperationExecutor Executor(IPipelineFunction function, TimeSpan grace) =>
        new([function], name => name == Descriptor.Name ? Descriptor : null, grace);

    private static Dictionary<string, string> Env() => new()
    {
        [PipelineFunctionRequest.WorkspaceVariable] = "/work/repo"
    };

    private sealed class FakeFunction(Func<CancellationToken, Task<PipelineFunctionResult>> body) : IPipelineFunction
    {
        public string Name => Descriptor.Name;

        public Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken) =>
            body(cancellationToken);
    }

    [Fact]
    public async Task ExternalCancellation_OfAFunctionStillRunningAfterTheGrace_IsNotAcknowledged()
    {
        var release = new TaskCompletionSource<PipelineFunctionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores its token: the stand-in for a synchronous publication block that keeps mutating.
        var function = new FakeFunction(_ =>
        {
            started.SetResult();
            return release.Task;
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = Executor(function, TimeSpan.FromMilliseconds(200)).ExecuteAsync(
            OperationKind.PipelineFunction, Descriptor.Name, Env(), 30, Capture, cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();

        try
        {
            var thrown = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

            Assert.IsType<PipelineFunctionNotStoppedException>(thrown);
        }
        finally
        {
            release.SetResult(PipelineFunctionResult.Success());
        }
    }

    [Fact]
    public async Task ExternalCancellation_IsAcknowledgedOnlyOnceTheFunctionReallyEnded()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = false;
        // Ends a moment after its token is cancelled, as a function that finishes its current mutation.
        var function = new FakeFunction(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
            await Task.Delay(300, CancellationToken.None);
            ended = true;
            throw new OperationCanceledException(token);
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = Executor(function, TimeSpan.FromSeconds(30)).ExecuteAsync(
            OperationKind.PipelineFunction, Descriptor.Name, Env(), 30, Capture, cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();

        var thrown = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.IsNotAssignableFrom<UnconfirmedCancellationException>(thrown);
        Assert.True(ended, "The executor acknowledged the cancellation while the function was still running.");
    }

    [Fact]
    public async Task Timeout_OfAFunctionStillRunningAfterTheGrace_SaysSoInTheLog()
    {
        var release = new TaskCompletionSource<PipelineFunctionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = new FakeFunction(_ => release.Task);

        try
        {
            var result = await Executor(function, TimeSpan.FromMilliseconds(200)).ExecuteAsync(
                OperationKind.PipelineFunction, Descriptor.Name, Env(), 0, Capture, TestContext.Current.CancellationToken);

            Assert.True(result.TimedOut);
            Assert.Contains(_log, entry => entry.Level == TaskLogLevel.Error
                && entry.Line.Contains("did not end", StringComparison.Ordinal));
        }
        finally
        {
            release.SetResult(PipelineFunctionResult.Success());
        }
    }
}
