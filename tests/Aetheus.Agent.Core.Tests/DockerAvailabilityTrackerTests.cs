// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class DockerAvailabilityTrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Observe_IndeterminateWithoutAnyConfirmation_ReportsUnavailable()
    {
        var tracker = new DockerAvailabilityTracker(_time);

        Assert.False(tracker.Observe(DockerProbeOutcome.Indeterminate));
    }

    [Fact]
    public void Observe_IndeterminateWithinHoldAfterAvailable_KeepsAvailable()
    {
        var tracker = new DockerAvailabilityTracker(_time);
        Assert.True(tracker.Observe(DockerProbeOutcome.Available));

        _time.Advance(AgentRuntimeDefaults.DockerProbeIndeterminateHold);

        Assert.True(tracker.Observe(DockerProbeOutcome.Indeterminate));
    }

    [Fact]
    public void Observe_IndeterminatePastHold_ReportsUnavailable()
    {
        var tracker = new DockerAvailabilityTracker(_time);
        tracker.Observe(DockerProbeOutcome.Available);

        _time.Advance(AgentRuntimeDefaults.DockerProbeIndeterminateHold + TimeSpan.FromSeconds(1));

        Assert.False(tracker.Observe(DockerProbeOutcome.Indeterminate));
    }

    [Fact]
    public void Observe_DefinitiveUnavailable_ReportsUnavailableAtOnceAndIsNotHeldAsAvailable()
    {
        var tracker = new DockerAvailabilityTracker(_time);
        tracker.Observe(DockerProbeOutcome.Available);

        Assert.False(tracker.Observe(DockerProbeOutcome.Unavailable));
        Assert.False(tracker.Observe(DockerProbeOutcome.Indeterminate));
    }

    [Fact]
    public async Task ProbeAsync_TimedOutCandidate_IsIndeterminateAndStopsProbing()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromException<ShellExecResult>(new OperationCanceledException("probe timeout")));

        var outcome = await DockerProbe.ProbeAsync(shell, TestContext.Current.CancellationToken);

        Assert.Equal(DockerProbeOutcome.Indeterminate, outcome);
        await shell.Received(1).RunExecAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task ProbeAsync_MissingBinaryOnEveryCandidate_IsUnavailable()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromException<ShellExecResult>(new System.ComponentModel.Win32Exception(2)));

        Assert.Equal(DockerProbeOutcome.Unavailable, await DockerProbe.ProbeAsync(shell, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProbeAsync_CallerCancellation_IsPropagatedNotReadAsAbsence()
    {
        var shell = Substitute.For<IShellRunner>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromException<ShellExecResult>(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DockerProbe.ProbeAsync(shell, cts.Token));
        Assert.False(await DockerProbe.IsAvailableAsync(shell, cts.Token));
    }
}
