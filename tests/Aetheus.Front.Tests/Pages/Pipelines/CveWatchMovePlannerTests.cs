// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The pure rules of a drop of the CVE watch block: the time is read in the zone of the watch whatever the reader's zone is.</summary>
public sealed class CveWatchMovePlannerTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    private static ScheduledServerActionDto Watch(bool movable = true, string? time = "04:00", string? zone = "Europe/Paris") => new()
    {
        Id = "cve:0",
        Kind = ServerActionKind.CveWatch,
        DailyTime = time,
        DailyTimeZoneId = zone,
        Movable = movable
    };

    private static DateTimeOffset At(int month, int day, int hour, int minute, TimeSpan offset) => new(2026, month, day, hour, minute, 0, offset);

    [Fact]
    public void AReaderInTheZoneOfTheWatch_MovesItToTheTimeOfTheDrop()
    {
        var decision = CveWatchMovePlanner.Decide(Watch(), At(11, 3, 4, 0, TimeSpan.FromHours(1)), At(11, 3, 5, 30, TimeSpan.FromHours(1)), Paris);

        Assert.Null(decision.Refusal);
        Assert.Equal("05:30", decision.PassTime);
        Assert.Equal(new TimeOnly(5, 30), decision.LocalTime);
    }

    [Fact]
    public void AReaderInAnotherZone_MovesItToTheDroppedInstantAtTheClockOfTheWatch()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // 23:30 in New York (UTC-5 in November) is 05:30 in Paris.
        var decision = CveWatchMovePlanner.Decide(
            Watch(), At(11, 2, 22, 0, TimeSpan.FromHours(-5)), At(11, 2, 23, 30, TimeSpan.FromHours(-5)), newYork);

        Assert.Equal("05:30", decision.PassTime);
        Assert.Equal(new TimeOnly(23, 30), decision.LocalTime);
    }

    [Fact]
    public void TheDateOfTheDropIsIgnored_AndADropOnTheSameTimeChangesNothing()
    {
        var decision = CveWatchMovePlanner.Decide(Watch(), At(11, 3, 4, 0, TimeSpan.FromHours(1)), At(11, 5, 4, 0, TimeSpan.FromHours(1)), Paris);

        Assert.Null(decision.Refusal);
        Assert.Null(decision.PassTime);
        Assert.True(decision.DateIgnored);
    }

    [Fact]
    public void ABlockThePlanDidNotGiveAsMovable_IsRefused()
    {
        var decision = CveWatchMovePlanner.Decide(Watch(movable: false), At(11, 3, 4, 0, TimeSpan.FromHours(1)), At(11, 3, 5, 0, TimeSpan.FromHours(1)), Paris);

        Assert.Equal("SchedulePermissionDenied", decision.Refusal);
        Assert.Null(decision.PassTime);
    }

    [Theory]
    [InlineData(null, "Europe/Paris")]
    [InlineData("04:00", null)]
    [InlineData("04:00", "Nowhere/Land")]
    public void AWatchWithNoReadableTimeOrZone_IsRefused_NeverWrittenApproximately(string? time, string? zone)
    {
        var decision = CveWatchMovePlanner.Decide(Watch(time: time, zone: zone), At(11, 3, 4, 0, TimeSpan.FromHours(1)), At(11, 3, 5, 0, TimeSpan.FromHours(1)), Paris);

        Assert.Equal("ScheduleMoveCustom", decision.Refusal);
        Assert.Null(decision.PassTime);
    }
}
