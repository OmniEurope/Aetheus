// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-46: the planner's view and day in the URL are read strictly; anything else means "not chosen".</summary>
public sealed class PipelineSchedulePlanUrlTests
{
    [Theory]
    [InlineData("night", "night")]
    [InlineData("day", "day")]
    [InlineData("week", "week")]
    [InlineData("cards", "cards")]
    [InlineData("Week", null)]
    [InlineData("Day", null)]
    [InlineData("bogus", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseView_KnowsTheFourViewsOnly(string? value, string? expected) =>
        Assert.Equal(expected, PipelineSchedulePlanUrl.ParseView(value));

    [Fact]
    public void ParseDate_RoundTripsAnIsoDay()
    {
        var day = new DateOnly(2026, 11, 3);

        Assert.Equal(day, PipelineSchedulePlanUrl.ParseDate(PipelineSchedulePlanUrl.FormatDate(day)));
    }

    [Theory]
    [InlineData("03/11/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-11-03T10:00")]
    [InlineData("not-a-day")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseDate_IgnoresAnythingButAnIsoDay(string? value) =>
        Assert.Null(PipelineSchedulePlanUrl.ParseDate(value));

    [Fact]
    public void AtMidday_AndDayOf_AgreeInTheReadersZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus-nine", TimeSpan.FromHours(9), "Test", "Test");
        var day = new DateOnly(2026, 11, 3);

        Assert.Equal(day, PipelineSchedulePlanUrl.DayOf(PipelineSchedulePlanUrl.AtMidday(day, zone), zone));
    }
}
