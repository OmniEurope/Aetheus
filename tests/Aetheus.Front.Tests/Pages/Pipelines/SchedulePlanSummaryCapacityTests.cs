// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-39: the overlap tile counts only the pairs of runs that exceed the execution capacity, and only
/// those still to come (the plan's window reaches back to the start of the week, R-43).</summary>
public sealed class SchedulePlanSummaryCapacityTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int hour, int minute = 0) => new(2026, 11, 3, hour, minute, 0, DateTimeKind.Utc);

    // The server flags every run that exceeds the capacity: the plan the page reads says which.
    private static ScheduledTaskDto Task(int id, DateTime start, DateTime end, int overlapMinutes) => new()
    {
        PipelineId = id,
        PipelineName = $"p{id}",
        Enabled = true,
        Occurrences = [new() { StartUtc = start, EndUtc = end, OverlapMinutes = overlapMinutes }]
    };

    private static SchedulePlanDto Plan(int capacity, params ScheduledTaskDto[] tasks) =>
        new() { FromUtc = Now.AddDays(-2), ToUtc = Now.AddDays(7), ExecutionCapacity = capacity, Tasks = [.. tasks] };

    [Fact]
    public void WithRoomForOneRun_TwoRunsAtOnceAreOnePair()
    {
        var plan = Plan(1, Task(1, At(1), At(2), 30), Task(2, At(1, 30), At(2, 30), 30));

        var summary = SchedulePlanSummary.Build(plan, Utc, Now);

        var pair = Assert.Single(summary.Overlaps);
        Assert.Equal(30, pair.Minutes);
        Assert.True(summary.HasOverlap(1));
    }

    [Fact]
    public void WithRoomForTwoRuns_TwoRunsAtOnceAreNoPair()
    {
        // Flagged as if the server had not known the capacity: the page checks the peak itself.
        var plan = Plan(2, Task(1, At(1), At(2), 30), Task(2, At(1, 30), At(2, 30), 30));

        var summary = SchedulePlanSummary.Build(plan, Utc, Now);

        Assert.Empty(summary.Overlaps);
        Assert.False(summary.HasOverlap(1));
    }

    [Fact]
    public void WithRoomForTwoRuns_ThreeRunsAtOnceAreThreePairs()
    {
        var plan = Plan(2,
            Task(1, At(1), At(3), 60),
            Task(2, At(1), At(3), 60),
            Task(3, At(2), At(3), 60));

        var summary = SchedulePlanSummary.Build(plan, Utc, Now);

        Assert.Equal(3, summary.Overlaps.Count);
        Assert.All([1, 2, 3], id => Assert.True(summary.HasOverlap(id)));
    }

    [Fact]
    public void WithRoomForTwoRuns_TwoPairsThatNeverMeetATripleAreNotPairs()
    {
        // 1 and 2 share 01:00-02:00, 3 and 4 share 02:00-03:00: two at a time, never three.
        var plan = Plan(2,
            Task(1, At(1), At(2), 60),
            Task(2, At(1), At(2), 60),
            Task(3, At(2), At(3), 60),
            Task(4, At(2), At(3), 60));

        Assert.Empty(SchedulePlanSummary.Build(plan, Utc, Now).Overlaps);
    }

    [Fact]
    public void AnOverlapThatEndedBeforeNow_IsThePastAndIsNotCounted()
    {
        var plan = Plan(1,
            new ScheduledTaskDto { PipelineId = 1, PipelineName = "p1", Enabled = true, Occurrences = [new() { StartUtc = Now.AddHours(-5), EndUtc = Now.AddHours(-4), OverlapMinutes = 60 }] },
            new ScheduledTaskDto { PipelineId = 2, PipelineName = "p2", Enabled = true, Occurrences = [new() { StartUtc = Now.AddHours(-5), EndUtc = Now.AddHours(-4), OverlapMinutes = 60 }] });

        Assert.Empty(SchedulePlanSummary.Build(plan, Utc, Now).Overlaps);
    }
}
