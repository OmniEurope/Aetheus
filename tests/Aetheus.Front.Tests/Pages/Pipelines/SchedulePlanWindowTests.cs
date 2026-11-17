// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-43: the plan the page reads holds the night, the day and the week the reader opens, so the calendar
/// draws from it and a new plan after a write is a redraw, not a second read of the range.</summary>
public sealed class SchedulePlanWindowTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
    private static readonly TimeOnly NightEnd = new(8, 0);

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void TheWindow_StartsBeforeTheMondayMidnightOfTheWeek_AndReachesTheLookAhead()
    {
        // Wednesday 2026-11-04 14:00 UTC, 15:00 in Paris; weeks start on Monday.
        var (from, to) = SchedulePlanWindow.For(Utc(11, 4, 14), Paris, 7, DayOfWeek.Monday);

        // Monday 00:00 in Paris is Sunday 23:00 UTC; the window takes two hours more for a clock change.
        Assert.Equal(Utc(11, 1, 21), from);
        Assert.Equal(Utc(11, 11, 14), to);
    }

    [Fact]
    public void BeforeEightInTheMorning_TheNightInProgressBeganTheEveningBefore_AndTheWeekStartIsTheEarlier()
    {
        // Monday 2026-11-02 05:00 in Paris: the night began Sunday evening, before this week's Monday midnight.
        var (from, _) = SchedulePlanWindow.For(Utc(11, 2, 4), Paris, 7, DayOfWeek.Monday);

        // Sunday 00:00 in Paris is Saturday 23:00 UTC, two hours earlier still.
        Assert.Equal(Utc(10, 31, 21), from);
    }

    [Theory]
    [InlineData(PipelineSchedulePlanView.Night)]
    [InlineData(PipelineSchedulePlanView.Day)]
    [InlineData(PipelineSchedulePlanView.Week)]
    public void TheDefaultViewsOfToday_AreInsideTheWindow(string mode)
    {
        var now = Utc(11, 4, 14);
        var (from, to) = SchedulePlanWindow.For(now, Paris, 7, DayOfWeek.Monday);
        var plan = new SchedulePlanDto { FromUtc = from, ToUtc = to };

        var span = SchedulePlanWindow.Visible(mode, new DateOnly(2026, 11, 4), Paris, DayOfWeek.Monday, NightEnd);

        Assert.True(SchedulePlanWindow.Covers(plan, span), $"{mode}: {span.StartUtc:O} to {span.EndUtc:O} against {from:O} to {to:O}.");
    }

    [Fact]
    public void TheVisibleSpans_AreTheOnesOmniSchedulerLoads()
    {
        var day = new DateOnly(2026, 11, 4);

        // Night: from the day's midnight to the morning after (the grid crosses midnight). Paris is UTC+1 in November.
        Assert.Equal((Utc(11, 3, 23), Utc(11, 5, 7)), SchedulePlanWindow.Visible(PipelineSchedulePlanView.Night, day, Paris, DayOfWeek.Monday, NightEnd));
        Assert.Equal((Utc(11, 3, 23), Utc(11, 4, 23)), SchedulePlanWindow.Visible(PipelineSchedulePlanView.Day, day, Paris, DayOfWeek.Monday, NightEnd));
        Assert.Equal((Utc(11, 1, 23), Utc(11, 8, 23)), SchedulePlanWindow.Visible(PipelineSchedulePlanView.Week, day, Paris, DayOfWeek.Monday, NightEnd));
        Assert.Equal((Utc(10, 31, 23), Utc(11, 7, 23)), SchedulePlanWindow.Visible(PipelineSchedulePlanView.Week, day, Paris, DayOfWeek.Sunday, NightEnd));
    }

    [Fact]
    public void ADayBeyondTheLookAhead_IsNotInTheWindow()
    {
        var (from, to) = SchedulePlanWindow.For(Utc(11, 4, 14), Paris, 7, DayOfWeek.Monday);
        var plan = new SchedulePlanDto { FromUtc = from, ToUtc = to };

        var farDay = SchedulePlanWindow.Visible(PipelineSchedulePlanView.Day, new DateOnly(2026, 11, 20), Paris, DayOfWeek.Monday, NightEnd);
        var pastWeek = SchedulePlanWindow.Visible(PipelineSchedulePlanView.Week, new DateOnly(2026, 10, 20), Paris, DayOfWeek.Monday, NightEnd);

        Assert.False(SchedulePlanWindow.Covers(plan, farDay));
        Assert.False(SchedulePlanWindow.Covers(plan, pastWeek));
    }

    [Fact]
    public void AroundTheAutumnClockChange_TheMidnightsAreTheLocalOnes()
    {
        // 2026-10-25: Paris goes back to UTC+1 at 03:00; midnight that day is still UTC+2, the next one UTC+1.
        var span = SchedulePlanWindow.Visible(PipelineSchedulePlanView.Day, new DateOnly(2026, 10, 25), Paris, DayOfWeek.Monday, NightEnd);

        Assert.Equal((Utc(10, 24, 22), Utc(10, 25, 23)), span);
    }
}
