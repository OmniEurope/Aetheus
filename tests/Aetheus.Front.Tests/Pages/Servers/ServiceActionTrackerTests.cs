// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary> A success stays on its row long enough to be read, even when the service list that
/// reflects it arrives a moment after (the success itself refreshes the server). </summary>
public class ServiceActionTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static TaskCompletedNotification Done(int taskId, TaskExecutionStatus status, int? exitCode = null) =>
        new() { ServerId = 1, TaskId = taskId, Status = status, ExitCode = exitCode };

    [Fact]
    public void ASuccess_SurvivesAListArrivingBeforeItWasShownLongEnough()
    {
        var tracker = new ServiceActionTracker();
        tracker.Track("nginx", 5, "Start");
        Assert.Equal("nginx", tracker.Complete(Done(5, TaskExecutionStatus.Success), T0));

        tracker.ForgetSucceeded(T0 + ServiceActionTracker.SuccessShownAtLeast - TimeSpan.FromMilliseconds(1));

        Assert.Equal(ServiceActionPhase.Succeeded, tracker.Of("nginx", [])!.Phase);
    }

    [Fact]
    public void ASuccess_GoesWithTheFirstListOnceItHasBeenShown()
    {
        var tracker = new ServiceActionTracker();
        tracker.Track("nginx", 5, "Start");
        tracker.Complete(Done(5, TaskExecutionStatus.Success), T0);

        tracker.ForgetSucceeded(T0 + ServiceActionTracker.SuccessShownAtLeast);

        Assert.Null(tracker.Of("nginx", []));
    }

    [Fact]
    public void AFailure_StaysWhateverTheTime()
    {
        var tracker = new ServiceActionTracker();
        tracker.Track("nginx", 5, "Restart");
        tracker.Complete(Done(5, TaskExecutionStatus.Failed, exitCode: 3), T0);

        tracker.ForgetSucceeded(T0 + TimeSpan.FromHours(1));

        var state = tracker.Of("nginx", [])!;
        Assert.Equal(ServiceActionPhase.Failed, state.Phase);
        Assert.Equal(3, state.ExitCode);
    }

    [Fact]
    public void AnActionInFlight_IsNeverForgotten()
    {
        var tracker = new ServiceActionTracker();
        tracker.Track("nginx", 5, "Stop");

        tracker.ForgetSucceeded(T0 + TimeSpan.FromHours(1));

        Assert.True(tracker.Of("nginx", [])!.InFlight);
    }

    [Fact]
    public void ANewActionAfterASuccess_IsNotForgottenWithIt()
    {
        var tracker = new ServiceActionTracker();
        tracker.Track("nginx", 5, "Start");
        tracker.Complete(Done(5, TaskExecutionStatus.Success), T0);
        tracker.Track("nginx", 6, "Stop");

        tracker.ForgetSucceeded(T0 + TimeSpan.FromHours(1));

        var state = tracker.Of("nginx", [])!;
        Assert.Equal(6, state.TaskId);
        Assert.True(state.InFlight);
    }
}
