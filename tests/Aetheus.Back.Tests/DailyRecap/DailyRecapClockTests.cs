// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.DailyRecap;

namespace Aetheus.Back.Tests;

/// <summary>The daily recap's calendar: the previous local day across timezones and DST, and when a send is due.</summary>
public sealed class DailyRecapClockTests
{
    private static TimeZoneInfo Zone(string id)
    {
        Assert.True(DailyRecapClock.TryResolveTimeZone(id, out var zone), $"{id} is not known to this host");
        return zone;
    }

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void DayWindow_OfAnOrdinaryParisDay_IsTwentyFourHoursFromLocalMidnight()
    {
        var (start, end) = DailyRecapClock.DayWindowUtc(new DateOnly(2026, 10, 8), Zone("Europe/Paris"));

        Assert.Equal(Utc(2026, 10, 7, 22), start);
        Assert.Equal(Utc(2026, 10, 8, 22), end);
    }

    [Fact]
    public void DayWindow_OfTheSpringChange_LastsTwentyThreeHours()
    {
        var (start, end) = DailyRecapClock.DayWindowUtc(new DateOnly(2026, 3, 29), Zone("Europe/Paris"));

        Assert.Equal(Utc(2026, 3, 28, 23), start);
        Assert.Equal(Utc(2026, 3, 29, 22), end);
        Assert.Equal(TimeSpan.FromHours(23), end - start);
    }

    [Fact]
    public void DayWindow_OfTheAutumnChange_LastsTwentyFiveHours()
    {
        var (start, end) = DailyRecapClock.DayWindowUtc(new DateOnly(2026, 10, 25), Zone("Europe/Paris"));

        Assert.Equal(Utc(2026, 10, 24, 22), start);
        Assert.Equal(Utc(2026, 10, 25, 23), end);
        Assert.Equal(TimeSpan.FromHours(25), end - start);
    }

    [Fact]
    public void DayWindow_FollowsTheConfiguredZone_NotTheServerClock()
    {
        var (newYorkStart, _) = DailyRecapClock.DayWindowUtc(new DateOnly(2026, 10, 8), Zone("America/New_York"));
        var (utcStart, utcEnd) = DailyRecapClock.DayWindowUtc(new DateOnly(2026, 10, 8), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 10, 8, 4), newYorkStart);
        Assert.Equal(Utc(2026, 10, 8, 0), utcStart);
        Assert.Equal(Utc(2026, 10, 9, 0), utcEnd);
    }

    [Fact]
    public void LocalToday_TurnsAtLocalMidnight()
    {
        var paris = Zone("Europe/Paris");

        Assert.Equal(new DateOnly(2026, 10, 8), DailyRecapClock.LocalToday(Utc(2026, 10, 8, 21, 59), paris));
        Assert.Equal(new DateOnly(2026, 10, 9), DailyRecapClock.LocalToday(Utc(2026, 10, 8, 22, 0), paris));
    }

    [Fact]
    public void Decide_IsDueAtTheSendTime_AndCoversThePreviousDay()
    {
        // 05:00 UTC is 07:00 in Paris (summer time).
        var decision = DailyRecapClock.Decide(Utc(2026, 10, 9, 5), true, new TimeOnly(7, 0), Zone("Europe/Paris"), new DateOnly(2026, 10, 8));

        Assert.Equal(DailyRecapScheduleOutcome.Due, decision.Outcome);
        Assert.Equal(new DateOnly(2026, 10, 9), decision.Today);
        Assert.Equal(new DateOnly(2026, 10, 8), decision.CoveredDay);
    }

    [Fact]
    public void Decide_IsNotDueOneMinuteBefore()
    {
        var decision = DailyRecapClock.Decide(Utc(2026, 10, 9, 4, 59), true, new TimeOnly(7, 0), Zone("Europe/Paris"), null);

        Assert.Equal(DailyRecapScheduleOutcome.NotYet, decision.Outcome);
    }

    [Fact]
    public void Decide_ReadsTheSendTimeInTheConfiguredZone()
    {
        // The same instant is 07:00 in Paris but 01:00 in New York.
        var decision = DailyRecapClock.Decide(Utc(2026, 10, 9, 5), true, new TimeOnly(7, 0), Zone("America/New_York"), null);

        Assert.Equal(DailyRecapScheduleOutcome.NotYet, decision.Outcome);
    }

    [Fact]
    public void Decide_NeverSendsTheSameDayTwice()
    {
        var decision = DailyRecapClock.Decide(Utc(2026, 10, 9, 15), true, new TimeOnly(7, 0), Zone("Europe/Paris"), new DateOnly(2026, 10, 9));

        Assert.Equal(DailyRecapScheduleOutcome.AlreadySent, decision.Outcome);
    }

    [Fact]
    public void Decide_AMissedSendTimeIsStillDueLaterTheSameDay()
    {
        // The backend was down at 07:00 and comes back at 10:30: yesterday's recap still goes, once.
        var decision = DailyRecapClock.Decide(Utc(2026, 10, 9, 8, 30), true, new TimeOnly(7, 0), Zone("Europe/Paris"), new DateOnly(2026, 10, 7));

        Assert.Equal(DailyRecapScheduleOutcome.Due, decision.Outcome);
        Assert.Equal(new DateOnly(2026, 10, 8), decision.CoveredDay);
    }

    [Fact]
    public void Decide_SendsNothingWhenSwitchedOff()
    {
        var decision = DailyRecapClock.Decide(Utc(2026, 10, 9, 8), false, new TimeOnly(7, 0), Zone("Europe/Paris"), null);

        Assert.Equal(DailyRecapScheduleOutcome.Disabled, decision.Outcome);
    }

    [Fact]
    public void UnknownZone_IsRefused()
    {
        Assert.False(DailyRecapClock.TryResolveTimeZone("Mars/Olympus_Mons", out _));
        Assert.False(DailyRecapClock.TryResolveTimeZone("  ", out _));
    }
}
