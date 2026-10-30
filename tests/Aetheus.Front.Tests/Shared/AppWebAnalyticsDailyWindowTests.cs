// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-30: the daily audience chart starts on the last 7 days and the OE range handles choose the range.</summary>
public sealed class AppWebAnalyticsDailyWindowTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AppWebAnalyticsDailyWindowTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Theory]
    [InlineData(30, 23)]
    [InlineData(8, 1)]
    [InlineData(5, 0)]
    [InlineData(0, 0)]
    public void DefaultRangeStart_IsTheLastSevenDays(int count, int expectedStart) =>
        Assert.Equal(expectedStart, AppWebAnalyticsView.DefaultRangeStart(count));

    [Fact]
    public void DailyChart_HasRangeHandlesAndNoWeekButtons_StartingOnTheLastSevenDays()
    {
        var first = new DateOnly(2026, 9, 1);
        _handler.SetJsonResponse("api/appmonitoring/apps/5/web-analytics?days=30", new AppWebAnalyticsSummaryDto
        {
            Daily = Enumerable.Range(0, 30)
                .Select(i => new AppWebAnalyticsPointDto { DayUtc = first.AddDays(i), UniqueVisitors = i + 1, PageViews = i + 2 })
                .ToList()
        });

        var cut = Render<AppWebAnalyticsView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".web-analytics-range")));
        Assert.Empty(cut.FindAll(".web-analytics-week-nav"));
        var navigator = cut.Find(".web-analytics-range");
        var handles = navigator.QuerySelectorAll("[role=slider]");
        Assert.Equal(2, handles.Length);
        Assert.Equal("23", handles[0].GetAttribute("aria-valuenow"));
        Assert.Equal("29", handles[1].GetAttribute("aria-valuenow"));
    }
}
