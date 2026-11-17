// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Dev;

namespace Aetheus.Back.Tests.Dev;

public class SchemaResetGateTests
{
    [Fact]
    public void WaitUntilOpen_WhenNoResetRuns_CompletesAtOnce()
    {
        var gate = new SchemaResetGate();

        Assert.True(gate.WaitUntilOpenAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitUntilOpen_OutsideTheReset_WaitsUntilTheResetEnds()
    {
        var gate = new SchemaResetGate();
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = gate.RunClosedAsync(async () =>
        {
            resetStarted.SetResult();
            await finishReset.Task;
        }, CancellationToken.None);
        await resetStarted.Task;

        var outside = Task.Run(() => gate.WaitUntilOpenAsync(CancellationToken.None), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(outside.IsCompleted);

        finishReset.SetResult();
        await reset;
        await outside.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(gate.WaitUntilOpenAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitUntilOpen_InsideTheReset_DoesNotWait()
    {
        var gate = new SchemaResetGate();
        var passedInside = false;

        await gate.RunClosedAsync(async () =>
        {
            await gate.WaitUntilOpenAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            passedInside = true;
        }, CancellationToken.None);

        Assert.True(passedInside);
    }

    [Fact]
    public async Task RunClosed_WhenTheResetThrows_ReopensTheGate()
    {
        var gate = new SchemaResetGate();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunClosedAsync(() => throw new InvalidOperationException("reset failed"), CancellationToken.None));

        Assert.True(gate.WaitUntilOpenAsync(CancellationToken.None).IsCompletedSuccessfully);
    }
}
