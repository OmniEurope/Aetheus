// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// PLAN-005 lot 4, what a drop means for a schedule, pure: the zone is Europe/Paris, whose two changes of 2026 are
/// the 29th of March (02:00 becomes 03:00, summer time) and the 25th of October (03:00 becomes 02:00). The offset
/// of the day a run is on converts its time, so the same local time is a different UTC time on each side of a change.
/// </summary>
public sealed class ScheduleMovePlannerTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
    private static readonly TimeSpan Winter = TimeSpan.FromHours(1);
    private static readonly TimeSpan Summer = TimeSpan.FromHours(2);

    private static ScheduledTaskDto Task(ScheduleFrequency frequency, int? day, int hour, int minute) => new()
    {
        PipelineId = 1,
        PipelineName = "nightly",
        Enabled = true,
        Frequency = frequency,
        Day = day,
        Hour = hour,
        Minute = minute,
        CronExpression = $"{minute} {hour} * * *"
    };

    private static DateTimeOffset Paris2026(int month, int day, int hour, int minute) =>
        new(new DateTime(2026, month, day, hour, minute, 0), Paris.GetUtcOffset(new DateTime(2026, month, day, hour, minute, 0)));

    // ---------- the zone itself ----------

    [Fact]
    public void ParisChangesItsOffsetOnTheTwoDatesTheTestsRelyOn()
    {
        Assert.Equal(Winter, Paris.GetUtcOffset(new DateTime(2026, 3, 28, 12, 0, 0)));
        Assert.Equal(Summer, Paris.GetUtcOffset(new DateTime(2026, 3, 29, 12, 0, 0)));
        Assert.Equal(Summer, Paris.GetUtcOffset(new DateTime(2026, 10, 24, 12, 0, 0)));
        Assert.Equal(Winter, Paris.GetUtcOffset(new DateTime(2026, 10, 25, 12, 0, 0)));
    }

    // ---------- weekly: the day and the time of the drop, converted with the offset of the dropped day ----------

    [Fact]
    public void Weekly_AtTheSpringChange_TheSameLocalTimeIsAnHourEarlierInUtcOnceSummerTimeBegins()
    {
        var task = Task(ScheduleFrequency.Weekly, 6, 2, 30);
        var original = Paris2026(3, 28, 3, 30);

        // Saturday the 28th, 04:30 is 03:30 UTC; Sunday the 29th, 03:30 is 01:30 UTC and the day of the week follows UTC.
        var saturday = ScheduleMovePlanner.Decide(task, original, Paris2026(3, 28, 4, 30), Paris, canWrite: true);
        var sunday = ScheduleMovePlanner.Decide(task, original, Paris2026(3, 29, 3, 30), Paris, canWrite: true);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 6, 3, 30), saturday.Spec);
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 0, 1, 30), sunday.Spec);
        Assert.Equal(0, sunday.LocalDay);
        Assert.Equal(new TimeOnly(3, 30), sunday.LocalTime);
    }

    [Fact]
    public void Weekly_ATimeThatTheSpringChangeSkips_IsTheFirstInstantAfterTheGap()
    {
        var task = Task(ScheduleFrequency.Weekly, 6, 2, 30);

        // 02:30 on Sunday the 29th does not exist in Paris: it is read as 03:30 (01:30 UTC), not written as an impossible time.
        var decision = ScheduleMovePlanner.Decide(task, Paris2026(3, 28, 3, 30), new DateTimeOffset(2026, 3, 29, 2, 30, 0, Winter), Paris, canWrite: true);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 0, 1, 30), decision.Spec);
    }

    [Fact]
    public void Weekly_AtTheAutumnChange_TheSameLocalTimeIsAnHourLaterInUtcOnceWinterTimeBegins()
    {
        var task = Task(ScheduleFrequency.Weekly, 6, 0, 0);
        var original = Paris2026(10, 24, 1, 0);

        // Saturday the 24th, 02:30 (summer) is 00:30 UTC; Monday the 26th, 02:30 (winter) is 01:30 UTC.
        var saturday = ScheduleMovePlanner.Decide(task, original, Paris2026(10, 24, 2, 30), Paris, canWrite: true);
        var monday = ScheduleMovePlanner.Decide(task, original, Paris2026(10, 26, 2, 30), Paris, canWrite: true);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 6, 0, 30), saturday.Spec);
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 1, 1, 30), monday.Spec);
    }

    [Fact]
    public void Weekly_ATimeThatTheAutumnChangeRepeats_IsReadAsWinterTime()
    {
        var task = Task(ScheduleFrequency.Weekly, 0, 0, 0);

        // 02:30 on Sunday the 25th happens twice; the first one is summer (00:30 UTC), the second winter (01:30 UTC).
        // The scheduler reports the wall time, so the one the zone resolves it to (standard time) is the one written.
        var decision = ScheduleMovePlanner.Decide(task, Paris2026(10, 18, 2, 0), new DateTimeOffset(2026, 10, 25, 2, 30, 0, Winter), Paris, canWrite: true);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 0, 1, 30), decision.Spec);
    }

    // ---------- the other frequencies: only the time, the offset of the moved run's own day ----------

    [Fact]
    public void EveryEvening_TheTimeIsConvertedWithTheOffsetOfTheDayOfTheMovedRun_AcrossBothChanges()
    {
        var task = Task(ScheduleFrequency.EveryEvening, null, 0, 0);

        // The run of Saturday the 28th of March (winter): 01:30 is 00:30 UTC. The run of Monday the 30th (summer): 01:30 is 23:30 UTC.
        var winter = ScheduleMovePlanner.Decide(task, Paris2026(3, 28, 1, 0), Paris2026(3, 28, 1, 30), Paris, canWrite: true);
        var summer = ScheduleMovePlanner.Decide(task, Paris2026(3, 30, 2, 0), Paris2026(3, 30, 1, 30), Paris, canWrite: true);
        // The run of Saturday the 24th of October (summer): 01:30 is 23:30 UTC. The run of Monday the 26th (winter): 01:30 is 00:30 UTC.
        var summerEnd = ScheduleMovePlanner.Decide(task, Paris2026(10, 24, 2, 0), Paris2026(10, 24, 1, 30), Paris, canWrite: true);
        var winterBack = ScheduleMovePlanner.Decide(task, Paris2026(10, 26, 1, 0), Paris2026(10, 26, 1, 30), Paris, canWrite: true);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.EveryEvening, null, 0, 30), winter.Spec);
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.EveryEvening, null, 23, 30), summer.Spec);
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.EveryEvening, null, 23, 30), summerEnd.Spec);
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.EveryEvening, null, 0, 30), winterBack.Spec);
        Assert.True(winter.CommonTime);
    }

    [Fact]
    public void EveryTwoDays_TheDateOfTheDropIsIgnored_OnlyItsTimeCounts()
    {
        var task = Task(ScheduleFrequency.EveryTwoDays, null, 1, 0);

        var decision = ScheduleMovePlanner.Decide(task, Paris2026(11, 3, 2, 0), Paris2026(11, 8, 2, 45), Paris, canWrite: true);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.EveryTwoDays, null, 1, 45), decision.Spec);
        Assert.True(decision.DateIgnored);
    }

    [Fact]
    public void Weekdays_ATimeThatFallsOnAnotherUtcDay_IsRefusedWithItsReason_InSummerTimeButNotInWinterTime()
    {
        var task = Task(ScheduleFrequency.Weekdays, null, 1, 0);

        // 01:30 Paris is 00:30 UTC in winter (same day, fine) and 23:30 UTC the day before in summer (refused).
        var winter = ScheduleMovePlanner.Decide(task, Paris2026(11, 3, 2, 0), Paris2026(11, 3, 1, 30), Paris, canWrite: true);
        var summer = ScheduleMovePlanner.Decide(task, Paris2026(7, 7, 3, 0), Paris2026(7, 7, 1, 30), Paris, canWrite: true);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekdays, null, 0, 30), winter.Spec);
        Assert.Null(summer.Spec);
        Assert.Equal("ScheduleWeekdaysImpossible", summer.Refusal);
    }

    [Fact]
    public void Monthly_ADayThatFallsOutsideOneToTwentyEightInUtc_IsRefusedWithItsReason()
    {
        var task = Task(ScheduleFrequency.Monthly, 1, 1, 0);

        // The 1st at 00:30 local is the last day of the previous month in UTC: no day the engine fires every month.
        var refused = ScheduleMovePlanner.Decide(task, Paris2026(11, 1, 2, 0), Paris2026(11, 1, 0, 30), Paris, canWrite: true);
        var kept = ScheduleMovePlanner.Decide(task, Paris2026(11, 1, 2, 0), Paris2026(11, 1, 2, 30), Paris, canWrite: true);

        Assert.Equal("ScheduleMonthDayImpossible", refused.Refusal);
        Assert.Null(refused.Spec);
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Monthly, 1, 1, 30), kept.Spec);
    }

    // ---------- nothing to write ----------

    [Fact]
    public void ADropAtTheOriginalPosition_ChangesNothing_AtEveryFrequencyAndOnBothSidesOfAChange()
    {
        // The stored UTC value is what is compared, so the same run dropped where it stands is unchanged in summer and in winter.
        foreach (var (frequency, day) in new (ScheduleFrequency, int?)[]
        {
            (ScheduleFrequency.EveryEvening, null), (ScheduleFrequency.EveryTwoDays, null), (ScheduleFrequency.Monthly, 3)
        })
        {
            var task = Task(frequency, day, 0, 30);
            Assert.Null(ScheduleMovePlanner.Decide(task, Paris2026(11, 3, 1, 30), Paris2026(11, 3, 1, 30), Paris, canWrite: true).Spec);
        }

        var weekly = Task(ScheduleFrequency.Weekly, 2, 0, 30);
        Assert.Null(ScheduleMovePlanner.Decide(weekly, Paris2026(11, 3, 1, 30), Paris2026(11, 3, 1, 30), Paris, canWrite: true).Spec);
        var summer = Task(ScheduleFrequency.EveryEvening, null, 23, 30);
        var decision = ScheduleMovePlanner.Decide(summer, Paris2026(7, 7, 1, 30), Paris2026(7, 7, 1, 30), Paris, canWrite: true);
        Assert.Null(decision.Spec);
        Assert.Null(decision.Refusal);
    }

    [Fact]
    public void AWeeklyDropOnAnotherDayAtTheSameTime_IsAWrite_ButAnotherDateOfACommonTimeTaskIsNot()
    {
        var weekly = Task(ScheduleFrequency.Weekly, 2, 0, 30);
        var evening = Task(ScheduleFrequency.EveryEvening, null, 0, 30);

        Assert.True(ScheduleMovePlanner.Decide(weekly, Paris2026(11, 3, 1, 30), Paris2026(11, 5, 1, 30), Paris, canWrite: true).IsWrite);
        var common = ScheduleMovePlanner.Decide(evening, Paris2026(11, 3, 1, 30), Paris2026(11, 5, 1, 30), Paris, canWrite: true);
        Assert.False(common.IsWrite);
        Assert.True(common.DateIgnored);
    }

    // ---------- refusals ----------

    [Fact]
    public void ACustomExpression_AndATaskWithoutASingleTime_CannotBeMoved()
    {
        var custom = Task(ScheduleFrequency.Custom, null, 0, 0);
        var noTime = custom with { Frequency = ScheduleFrequency.EveryEvening, Hour = null, Minute = null };

        Assert.Equal("ScheduleMoveCustom", ScheduleMovePlanner.Decide(custom, Paris2026(11, 3, 1, 0), Paris2026(11, 3, 2, 0), Paris, canWrite: true).Refusal);
        Assert.Equal("ScheduleMoveCustom", ScheduleMovePlanner.Decide(noTime, Paris2026(11, 3, 1, 0), Paris2026(11, 3, 2, 0), Paris, canWrite: true).Refusal);
    }

    [Fact]
    public void WithoutTheWriteRight_TheMoveIsRefused_BeforeAnythingElseIsRead()
    {
        var custom = Task(ScheduleFrequency.Custom, null, 0, 0);

        Assert.Equal("SchedulePermissionDenied", ScheduleMovePlanner.Decide(custom, Paris2026(11, 3, 1, 0), Paris2026(11, 3, 2, 0), Paris, canWrite: false).Refusal);
    }

    // ---------- the requests ----------

    [Fact]
    public void TheRequestOfAMove_IsWrittenAgainstWhatWasRead_AndThePreviousValueIsWhatUndoWrites()
    {
        var task = Task(ScheduleFrequency.Weekly, 2, 3, 0);
        var spec = new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 4, 3, 30);

        var request = ScheduleMovePlanner.RequestOf(task, spec);
        var previous = ScheduleMovePlanner.Previous(task)!;

        Assert.Equal((ScheduleFrequency.Weekly, 4, 3, 30, true), (request.Frequency!.Value, request.Day, request.Hour, request.Minute, request.Enabled));
        Assert.Equal((task.CronExpression, true), (request.ExpectedCronExpression, request.ExpectedEnabled));
        Assert.Equal((ScheduleFrequency.Weekly, 2, 3, 0), (previous.Frequency!.Value, previous.Day, previous.Hour, previous.Minute));
        Assert.Null(ScheduleMovePlanner.Previous(task with { Frequency = ScheduleFrequency.Custom }));
    }
}
