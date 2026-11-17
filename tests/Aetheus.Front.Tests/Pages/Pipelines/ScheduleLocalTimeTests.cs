// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// PLAN-005 lot 3, the conversion between the reader's clock and the UTC the engine reads. Pure: the zone and the
/// present are arguments, the zone is a fixed offset ahead of UTC, so the expectations are plain arithmetic.
/// </summary>
public sealed class ScheduleLocalTimeTests
{
    private static readonly TimeZoneInfo PlusOne = TimeZoneInfo.CreateCustomTimeZone("plus-one", TimeSpan.FromHours(1), "Plus one", "Plus one");
    private static readonly DateTime Monday = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ALocalTimeBecomesTheUtcTimeTheEngineReads()
    {
        var spec = ScheduleLocalTime.ToUtc(ScheduleFrequency.EveryEvening, null, new TimeOnly(2, 30), PlusOne, Monday);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.EveryEvening, null, 1, 30), spec);
    }

    [Fact]
    public void AWeeklyDayMovesBackWithTheTimeWhenTheUtcTimeFallsOnTheDayBefore()
    {
        // Monday 00:30 for a reader one hour ahead is Sunday 23:30 UTC: the file must say Sunday (0), not Monday.
        var spec = ScheduleLocalTime.ToUtc(ScheduleFrequency.Weekly, 1, new TimeOnly(0, 30), PlusOne, Monday);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 0, 23, 30), spec);
    }

    [Fact]
    public void AMonthlyDayThatFallsOutsideOneToTwentyEightInUtcIsRefused_NotWrittenWrong()
    {
        // The 1st at 00:30 local is the last day of the previous month in UTC: no day the engine fires every month.
        Assert.Null(ScheduleLocalTime.ToUtc(ScheduleFrequency.Monthly, 1, new TimeOnly(0, 30), PlusOne, Monday));
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Monthly, 15, 11, 0),
            ScheduleLocalTime.ToUtc(ScheduleFrequency.Monthly, 15, new TimeOnly(12, 0), PlusOne, Monday));
    }

    [Fact]
    public void AMondayToFridayRunThatFallsOnAnotherUtcDayIsRefused_NotWrittenTuesdayToSaturday()
    {
        // 00:30 local is 23:30 UTC the day before: "1-5" in UTC would run from Tuesday to Saturday for the reader.
        Assert.Null(ScheduleLocalTime.ToUtc(ScheduleFrequency.Weekdays, null, new TimeOnly(0, 30), PlusOne, Monday));
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekdays, null, 0, 30),
            ScheduleLocalTime.ToUtc(ScheduleFrequency.Weekdays, null, new TimeOnly(1, 30), PlusOne, Monday));
    }

    [Theory]
    [InlineData(2026, 3, 28, 1, 30, 0, 30)]   // the day before the spring change: UTC+1
    [InlineData(2026, 3, 29, 3, 30, 1, 30)]   // the day of the spring change, after the gap: UTC+2
    [InlineData(2026, 10, 24, 2, 30, 0, 30)]  // the day before the autumn change: UTC+2
    [InlineData(2026, 10, 25, 3, 30, 2, 30)]  // the day of the autumn change, after the repeated hour: UTC+1
    public void ATimeOnAGivenLocalDayIsConvertedWithTheOffsetOfThatDay_AtBothChangesOfEuropeParis2026(
        int year, int month, int day, int hour, int minute, int utcHour, int utcMinute)
    {
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

        var spec = ScheduleLocalTime.ToUtcOn(ScheduleFrequency.EveryEvening, new DateOnly(year, month, day), new TimeOnly(hour, minute), paris);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.EveryEvening, null, utcHour, utcMinute), spec);
    }

    [Fact]
    public void ATimeThatTheSpringChangeSkipsIsTheFirstInstantAfterTheGap()
    {
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

        // 02:30 on Sunday 2026-03-29 does not exist: it is 03:30 summer time, 01:30 UTC, a Sunday.
        var spec = ScheduleLocalTime.ToUtcOn(ScheduleFrequency.Weekly, new DateOnly(2026, 3, 29), new TimeOnly(2, 30), paris);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Weekly, 0, 1, 30), spec);
    }

    [Fact]
    public void ATaskIsReadBackInTheReadersClock_WithItsLocalDay()
    {
        var task = new ScheduledTaskDto
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            Day = 0,
            Hour = 23,
            Minute = 30,
            NextRunUtc = new DateTime(2026, 11, 8, 23, 30, 0, DateTimeKind.Utc)
        };

        var local = ScheduleLocalTime.ToLocal(task, PlusOne, Monday);

        // Sunday 23:30 UTC is Monday 00:30 locally.
        Assert.Equal(new LocalSchedule(new TimeOnly(0, 30), 1), local);
    }

    /// <summary>The next run of a monthly schedule as the engine fires it: day <paramref name="day"/> at the UTC time, this
    /// month if it is still ahead of <paramref name="nowUtc"/>, the next one otherwise.</summary>
    private static DateTime NextMonthlyRun(int day, int hour, int minute, DateTime nowUtc)
    {
        var thisMonth = new DateTime(nowUtc.Year, nowUtc.Month, day, hour, minute, 0, DateTimeKind.Utc);
        return thisMonth > nowUtc ? thisMonth : thisMonth.AddMonths(1);
    }

    private static ScheduledTaskDto Monthly(int day, int hour, int minute, DateTime nowUtc, bool enabled = true) => new()
    {
        Enabled = enabled,
        Frequency = ScheduleFrequency.Monthly,
        Day = day,
        Hour = hour,
        Minute = minute,
        NextRunUtc = enabled ? NextMonthlyRun(day, hour, minute, nowUtc) : null
    };

    [Theory]
    [InlineData(2026, 3, 20, 5, 10, 0)]    // next run Apr 5, after the spring change of Mar 29: shown 12:00 summer time
    [InlineData(2026, 3, 29, 5, 10, 0)]    // the day of the spring change itself
    [InlineData(2026, 10, 20, 5, 10, 0)]   // next run Nov 5, after the autumn change of Oct 25: shown 11:00 winter time
    [InlineData(2026, 10, 25, 5, 10, 0)]   // the day of the autumn change itself
    [InlineData(2026, 3, 10, 28, 10, 0)]   // next run Mar 28, the day before the spring change
    [InlineData(2026, 3, 20, 10, 23, 30)]  // next run Apr 10 23:30 UTC, the next local day (Apr 11 01:30) in summer
    [InlineData(2026, 10, 27, 1, 22, 30)]  // next run Nov 1 22:30 UTC, a local day later in winter
    [InlineData(2026, 7, 14, 15, 9, 15)]   // no change in sight
    public void AMonthlyScheduleSavedWithoutEdit_KeepsItsUtcTime_WhenItsNextRunCrossesADaylightSavingChange(
        int year, int month, int nowDay, int day, int hour, int minute)
    {
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var now = new DateTime(year, month, nowDay, 12, 0, 0, DateTimeKind.Utc);
        var task = Monthly(day, hour, minute, now);

        // What the dialog opens on, then what saving it unchanged writes.
        var local = ScheduleLocalTime.ToLocal(task, paris, now);
        var written = ScheduleLocalTime.ToUtc(ScheduleFrequency.Monthly, local!.Value.Day, local.Value.Time, paris, now);

        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Monthly, day, hour, minute), written);
    }

    [Fact]
    public void ADisabledMonthlySchedule_ShowsAndSavesTheSameOccurrence_AcrossTheChange()
    {
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var now = new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        var task = Monthly(5, 10, 0, now, enabled: false);

        var local = ScheduleLocalTime.ToLocal(task, paris, now);
        var written = ScheduleLocalTime.ToUtc(ScheduleFrequency.Monthly, local!.Value.Day, local.Value.Time, paris, now);

        // Mar 5 has passed: the occurrence is Apr 5 (summer time) for both, so 10:00 UTC reads back as 10:00 UTC.
        Assert.Equal(new TimeOnly(12, 0), local.Value.Time);
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Monthly, 5, 10, 0), written);
    }

    [Fact]
    public void AMonthlyDayStillAheadThisMonth_IsConvertedOnThisMonthsDay()
    {
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var now = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        // Mar 15 is before the change: 11:00 local is 10:00 UTC (UTC+1).
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Monthly, 15, 10, 0),
            ScheduleLocalTime.ToUtc(ScheduleFrequency.Monthly, 15, new TimeOnly(11, 0), paris, now));
    }

    [Fact]
    public void ACustomExpressionHasNoSingleLocalTime()
    {
        var task = new ScheduledTaskDto { Enabled = true, Frequency = ScheduleFrequency.Custom, CronExpression = "*/20 6 * * *" };

        Assert.Null(ScheduleLocalTime.ToLocal(task, PlusOne, Monday));
    }
}
