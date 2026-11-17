// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

public sealed class ScheduleOverlapFinderTests
{
    private static DateTime At(int day, int hour, int minute = 0) =>
        new(2026, 11, day, hour, minute, 0, DateTimeKind.Utc);

    private static ScheduledTaskDto Task(int id, params (DateTime Start, DateTime End)[] occurrences) => new()
    {
        PipelineId = id,
        Occurrences = occurrences.Select(o => new ScheduleOccurrenceDto { StartUtc = o.Start, EndUtc = o.End }).ToList()
    };

    private static List<int> Overlaps(ScheduledTaskDto task) => task.Occurrences.Select(o => o.OverlapMinutes).ToList();

    [Fact]
    public void Fill_PartialOverlap_FillsBothSidesWithTheSharedMinutes()
    {
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 2))),
            Task(2, (At(1, 1, 40), At(1, 2, 30)))]);

        Assert.Equal([20], Overlaps(result[0]));
        Assert.Equal([20], Overlaps(result[1]));
    }

    // Half-open intervals: end equal to the next start is not an overlap.
    [Fact]
    public void Fill_EdgeToEdge_IsNotAnOverlap()
    {
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 2))),
            Task(2, (At(1, 2), At(1, 3)))]);

        Assert.Equal([0], Overlaps(result[0]));
        Assert.Equal([0], Overlaps(result[1]));
    }

    // Short runs of a third task start between the long run and the occurrence it reaches.
    [Fact]
    public void Fill_ALongRunStartedEarlier_StillReachesALaterOccurrence()
    {
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 0), At(1, 6))),
            Task(2, (At(1, 1), At(1, 1, 5)), (At(1, 2), At(1, 2, 5)), (At(1, 3), At(1, 3, 5))),
            Task(3, (At(1, 5), At(1, 5, 30)))]);

        Assert.Equal([30], Overlaps(result[2]));
    }

    // Every-minute tasks at the expansion bound (measured 2026-11-01: 12 s before the scan skipped what cannot reach, under 0.5 s after).
    [Fact]
    public void Fill_ManyTasksAtTheOccurrenceBound_CountsEachOccurrence()
    {
        var start = At(1, 0);
        var tasks = Enumerable.Range(1, 60)
            .Select(id => Task(id, Enumerable.Range(0, ScheduleOccurrenceExpander.MaxOccurrences)
                .Select(minute => (start.AddMinutes(minute), start.AddMinutes(minute + 1)))
                .ToArray()))
            .ToList();

        var result = ScheduleOverlapFinder.Fill(tasks);

        Assert.All(result, task => Assert.All(task.Occurrences, o => Assert.Equal(1, o.OverlapMinutes)));
    }

    [Fact]
    public void Fill_OneMinuteOfSharedTime_IsAnOverlapOfOneMinute()
    {
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 2))),
            Task(2, (At(1, 1, 59), At(1, 3)))]);

        Assert.Equal([1], Overlaps(result[0]));
    }

    [Fact]
    public void Fill_ThreeTasks_CountsCoveredTimeOnceAndOnlyAgainstOtherTasks()
    {
        // Task 1: 01:00-03:00. Task 2: 01:30-02:00. Task 3: 01:45-02:30 (overlaps task 2 for 15 min).
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 3))),
            Task(2, (At(1, 1, 30), At(1, 2))),
            Task(3, (At(1, 1, 45), At(1, 2, 30)))]);

        Assert.Equal([60], Overlaps(result[0]));
        Assert.Equal([30], Overlaps(result[1]));
        Assert.Equal([45], Overlaps(result[2]));
    }

    [Fact]
    public void Fill_RunCrossingMidnight_OverlapsTheNextDaysTask()
    {
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 23, 30), At(2, 0, 45))),
            Task(2, (At(2, 0), At(2, 1)))]);

        Assert.Equal([45], Overlaps(result[0]));
        Assert.Equal([45], Overlaps(result[1]));
    }

    [Fact]
    public void Fill_OccurrencesOfTheSameTask_AreNeverComparedWithEachOther()
    {
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 3)), (At(1, 2), At(1, 4)))]);

        Assert.Equal([0, 0], Overlaps(result[0]));
    }

    [Fact]
    public void Fill_FillsEachOccurrenceSeparately_AndDoesNotModifyTheInput()
    {
        var first = Task(1, (At(1, 1), At(1, 2)), (At(2, 1), At(2, 2)));
        var second = Task(2, (At(2, 1, 30), At(2, 2, 30)));

        var result = ScheduleOverlapFinder.Fill([first, second]);

        Assert.Equal([0, 30], Overlaps(result[0]));
        Assert.Equal([30], Overlaps(result[1]));
        Assert.Equal([0, 0], Overlaps(first));
    }

    [Fact]
    public void Fill_NoTasks_GivesNoTasks()
    {
        Assert.Empty(ScheduleOverlapFinder.Fill([]));
    }

    // R-39: runs side by side within the capacity are not an overlap; only the time beyond it counts.
    [Fact]
    public void Fill_WithRoomForTwoRuns_TwoRunsAtOnceAreNotAnOverlap()
    {
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 2))),
            Task(2, (At(1, 1, 40), At(1, 2, 30)))], capacity: 2);

        Assert.Equal([0], Overlaps(result[0]));
        Assert.Equal([0], Overlaps(result[1]));
    }

    [Fact]
    public void Fill_WithRoomForTwoRuns_ThreeRunsAtOnceOverlapOnlyWhereAllThreeRun()
    {
        // 01:00-03:00, 01:30-03:00 and 02:00-02:30: all three run together from 02:00 to 02:30 only.
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 3))),
            Task(2, (At(1, 1, 30), At(1, 3))),
            Task(3, (At(1, 2), At(1, 2, 30)))], capacity: 2);

        Assert.Equal([30], Overlaps(result[0]));
        Assert.Equal([30], Overlaps(result[1]));
        Assert.Equal([30], Overlaps(result[2]));
    }

    [Fact]
    public void Fill_WithRoomForThreeRuns_ThreeRunsAtOnceAreNotAnOverlap()
    {
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 3))),
            Task(2, (At(1, 1), At(1, 3))),
            Task(3, (At(1, 1), At(1, 3)))], capacity: 3);

        Assert.All(result, task => Assert.Equal([0], Overlaps(task)));
    }

    [Fact]
    public void Fill_ACapacityOfOne_IsTheDefault_AndACapacityBelowOneCountsAsOne()
    {
        var tasks = new[] { Task(1, (At(1, 1), At(1, 2))), Task(2, (At(1, 1, 40), At(1, 2, 30))) };

        Assert.Equal([20], Overlaps(ScheduleOverlapFinder.Fill(tasks, capacity: 1)[0]));
        Assert.Equal([20], Overlaps(ScheduleOverlapFinder.Fill(tasks, capacity: 0)[0]));
        Assert.Equal([20], Overlaps(ScheduleOverlapFinder.Fill(tasks)[0]));
    }

    [Fact]
    public void Fill_WithRoomForTwoRuns_ARunEndingWhereAnotherStartsDoesNotCountWithIt()
    {
        // 01:00-02:00 and 02:00-03:00 never run together; a third run covers both: at most two at a time.
        var result = ScheduleOverlapFinder.Fill([
            Task(1, (At(1, 1), At(1, 2))),
            Task(2, (At(1, 2), At(1, 3))),
            Task(3, (At(1, 1), At(1, 3)))], capacity: 2);

        Assert.All(result, task => Assert.Equal([0], Overlaps(task)));
    }

    [Fact]
    public void PeakConcurrency_IsTheMostTasksRunningAtOnce_ClippedToTheSpan()
    {
        var tasks = new[]
        {
            Task(1, (At(1, 1), At(1, 3))),
            Task(2, (At(1, 1, 30), At(1, 2))),
            Task(3, (At(1, 2, 30), At(1, 4)))
        };

        Assert.Equal(2, ScheduleOverlapFinder.PeakConcurrency(tasks, At(1, 1), At(1, 4)));
        Assert.Equal(2, ScheduleOverlapFinder.PeakConcurrency(tasks, At(1, 1), At(1, 2)));
        Assert.Equal(2, ScheduleOverlapFinder.PeakConcurrency(tasks, At(1, 2, 30), At(1, 3)));
        Assert.Equal(1, ScheduleOverlapFinder.PeakConcurrency(tasks, At(1, 3, 15), At(1, 3, 45)));
        Assert.Equal(0, ScheduleOverlapFinder.PeakConcurrency(tasks, At(1, 5), At(1, 6)));
    }

    // R-8: a pipeline and a server action that run together overlap, whatever the server.
    [Fact]
    public void Fill_WithServerActions_APipelineAndAnActionOverlapEachOther_AndTwoActionsDoToo()
    {
        var action = new ScheduledServerActionDto
        {
            Id = "rkhunter:7",
            Occurrences = [new ScheduleOccurrenceDto { StartUtc = At(1, 1, 40), EndUtc = At(1, 2, 30) }]
        };
        var other = action with { Id = "rkhunter:8", Occurrences = [new ScheduleOccurrenceDto { StartUtc = At(1, 2, 10), EndUtc = At(1, 3) }] };

        var (tasks, actions) = ScheduleOverlapFinder.Fill([Task(1, (At(1, 1), At(1, 2)))], [action, other], capacity: 1);

        Assert.Equal([20], Overlaps(tasks[0]));
        // 01:40 to 02:30 shares 01:40 to 02:00 with the pipeline and 02:10 to 02:30 with the other action.
        Assert.Equal(40, actions[0].Occurrences[0].OverlapMinutes);
        Assert.Equal("rkhunter:7", actions[0].Id);
        Assert.Equal(20, actions[1].Occurrences[0].OverlapMinutes);
    }

    [Fact]
    public void Fill_WithServerActions_WithRoomForBoth_NothingOverlaps()
    {
        var action = new ScheduledServerActionDto
        {
            Id = "rkhunter:7",
            Occurrences = [new ScheduleOccurrenceDto { StartUtc = At(1, 1), EndUtc = At(1, 2) }]
        };

        var (tasks, actions) = ScheduleOverlapFinder.Fill([Task(1, (At(1, 1), At(1, 2)))], [action], capacity: 2);

        Assert.Equal([0], Overlaps(tasks[0]));
        Assert.Equal(0, actions[0].Occurrences[0].OverlapMinutes);
    }

    [Fact]
    public void Fill_WithRoomForTwoRuns_ManyTasksAtTheOccurrenceBound_CountsEachOccurrence()
    {
        var start = At(1, 0);
        var tasks = Enumerable.Range(1, 60)
            .Select(id => Task(id, Enumerable.Range(0, ScheduleOccurrenceExpander.MaxOccurrences)
                .Select(minute => (start.AddMinutes(minute), start.AddMinutes(minute + 1)))
                .ToArray()))
            .ToList();

        var result = ScheduleOverlapFinder.Fill(tasks, capacity: 2);

        Assert.All(result, task => Assert.All(task.Occurrences, o => Assert.Equal(1, o.OverlapMinutes)));
    }
}
