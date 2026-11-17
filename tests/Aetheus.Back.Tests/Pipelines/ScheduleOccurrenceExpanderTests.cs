// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

public sealed class ScheduleOccurrenceExpanderTests
{
    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Expand_NightlyCron_GivesOneOccurrencePerDayWithTheDuration()
    {
        var result = ScheduleOccurrenceExpander.Expand("0 30 2 * * *", Utc(2026, 11, 1), Utc(2026, 11, 4), 45);

        Assert.Equal(
            [Utc(2026, 11, 1, 2, 30), Utc(2026, 11, 2, 2, 30), Utc(2026, 11, 3, 2, 30)],
            result.Select(o => o.StartUtc));
        Assert.All(result, o => Assert.Equal(o.StartUtc.AddMinutes(45), o.EndUtc));
        Assert.All(result, o => Assert.Equal(0, o.OverlapMinutes));
    }

    [Fact]
    public void Expand_WindowIsHalfOpen_FromInclusiveToExclusive()
    {
        var result = ScheduleOccurrenceExpander.Expand("0 0 0 * * *", Utc(2026, 11, 1), Utc(2026, 11, 3), 60);

        Assert.Equal([Utc(2026, 11, 1), Utc(2026, 11, 2)], result.Select(o => o.StartUtc));
    }

    // The engine reads the cron in UTC: crossing the Paris change of 2027-03-28 (01:00 UTC) keeps the
    // UTC instants, only the local clock shifts by one hour.
    [Fact]
    public void Expand_WindowAcrossDaylightSavingChange_KeepsTheRealUtcInstants()
    {
        var result = ScheduleOccurrenceExpander.Expand("0 30 1 * * *", Utc(2027, 3, 27), Utc(2027, 3, 30), 60);

        Assert.Equal(
            [Utc(2027, 3, 27, 1, 30), Utc(2027, 3, 28, 1, 30), Utc(2027, 3, 29, 1, 30)],
            result.Select(o => o.StartUtc));
        Assert.All(result, o => Assert.Equal(DateTimeKind.Utc, o.StartUtc.Kind));
        var paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
        var local = result.Select(o => TimeZoneInfo.ConvertTimeFromUtc(o.StartUtc, paris).Hour).ToList();
        Assert.Equal([2, 3, 3], local);
    }

    [Fact]
    public void Expand_EverySecondCron_IsBoundedToMaxOccurrences()
    {
        var result = ScheduleOccurrenceExpander.Expand("* * * * * *", Utc(2026, 11, 1), Utc(2026, 11, 8), 1);

        Assert.Equal(ScheduleOccurrenceExpander.MaxOccurrences, result.Count);
        Assert.Equal(Utc(2026, 11, 1), result[0].StartUtc);
        Assert.Equal(Utc(2026, 11, 1).AddSeconds(ScheduleOccurrenceExpander.MaxOccurrences - 1), result[^1].StartUtc);
    }

    [Fact]
    public void ExpandBounded_MoreOccurrencesThanTheBound_SaysTheListIsCut()
    {
        var everyMinuteOverAWeek = ScheduleOccurrenceExpander.ExpandBounded("0 * * * * *", Utc(2026, 11, 1), Utc(2026, 11, 8), 1);

        Assert.True(everyMinuteOverAWeek.IsTruncated);
        Assert.Equal(ScheduleOccurrenceExpander.MaxOccurrences, everyMinuteOverAWeek.Occurrences.Count);
    }

    // The bound itself is complete: one more occurrence is what makes the list cut.
    [Theory]
    [InlineData(ScheduleOccurrenceExpander.MaxOccurrences, false)]
    [InlineData(ScheduleOccurrenceExpander.MaxOccurrences + 1, true)]
    public void ExpandBounded_AtTheBound_IsTruncatedOnlyWhenAnOccurrenceWasDropped(int minutes, bool truncated)
    {
        var from = Utc(2026, 11, 1);

        var result = ScheduleOccurrenceExpander.ExpandBounded("0 * * * * *", from, from.AddMinutes(minutes), 1);

        Assert.Equal(truncated, result.IsTruncated);
        Assert.Equal(ScheduleOccurrenceExpander.MaxOccurrences, result.Occurrences.Count);
    }

    [Fact]
    public void ExpandBounded_InvalidExpression_IsEmptyAndNotTruncated()
    {
        var result = ScheduleOccurrenceExpander.ExpandBounded("abc", Utc(2026, 11, 1), Utc(2026, 11, 8), 1);

        Assert.Empty(result.Occurrences);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void NextOccurrence_IsStrictlyAfterTheInstant()
    {
        Assert.Equal(Utc(2026, 11, 2, 2, 30), ScheduleOccurrenceExpander.NextOccurrence("0 30 2 * * *", Utc(2026, 11, 1, 2, 30)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void NextOccurrence_NoValidExpression_IsNull(string? cron)
    {
        Assert.Null(ScheduleOccurrenceExpander.NextOccurrence(cron, Utc(2026, 11, 1)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0 0 * * *")]
    [InlineData("99 99 99 * * *")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Expand_UnparsableOrEmptyExpression_GivesNoOccurrenceAndDoesNotThrow(string? cron)
    {
        var result = ScheduleOccurrenceExpander.Expand(cron, Utc(2026, 11, 1), Utc(2026, 11, 8), 60);

        Assert.Empty(result);
    }

    [Fact]
    public void Expand_EmptyOrInvertedWindow_GivesNoOccurrence()
    {
        Assert.Empty(ScheduleOccurrenceExpander.Expand("0 0 0 * * *", Utc(2026, 11, 2), Utc(2026, 11, 2), 60));
        Assert.Empty(ScheduleOccurrenceExpander.Expand("0 0 0 * * *", Utc(2026, 11, 3), Utc(2026, 11, 2), 60));
    }
}
