// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Recette R-22: the Visitors tab lists each daily visitor with first visit time and entry page.</summary>
public class AppWebAnalyticsVisitorsListTests : BunitContext
{
    private const string VisitorsUrl = "api/appmonitoring/apps/5/web-analytics/visitors?days=30";
    private readonly BunitTestHelper.TestHandler _handler;

    public AppWebAnalyticsVisitorsListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static readonly DateTime Early = new(2026, 11, 8, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Late = new(2026, 11, 8, 14, 45, 0, DateTimeKind.Utc);

    [Fact]
    public void Visitors_ShowFirstVisitTimeAndEntryPage_NewestFirstVisitFirst()
    {
        _handler.SetJsonResponse(VisitorsUrl, new List<AppWebAnalyticsVisitorDto>
        {
            new() { Visitor = "aaaa1111", FirstSeenAtUtc = Early, EntryRoute = "/login" },
            new() { Visitor = "bbbb2222", FirstSeenAtUtc = Late, EntryRoute = "/projects/{id}" }
        });

        var cut = Render<AppWebAnalyticsVisitorsList>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("VisitorFirstVisit", cut.Markup);
            Assert.Contains("VisitorEntryPage", cut.Markup);
            var rows = cut.FindAll(".omni-data-grid__table tbody tr[data-omni-row-index]");
            Assert.Equal(2, rows.Count);
            Assert.Contains("/projects/{id}", rows[0].TextContent);
            Assert.Contains(Late.ToLocalTime().ToString("g"), rows[0].TextContent);
            Assert.Contains("bbbb2222", rows[0].TextContent);
            Assert.Contains("/login", rows[1].TextContent);
            Assert.Contains(Early.ToLocalTime().ToString("g"), rows[1].TextContent);
        });
    }

    [Fact]
    public void Visitors_FirstVisitAndEntryPageColumns_AreSortable()
    {
        _handler.SetJsonResponse(VisitorsUrl, new List<AppWebAnalyticsVisitorDto>
        {
            new() { Visitor = "aaaa1111", FirstSeenAtUtc = Early, EntryRoute = "/login" }
        });

        var cut = Render<AppWebAnalyticsVisitorsList>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("th[data-omni-col='FirstSeenAtUtc'] .omni-data-grid__sort"));
            Assert.NotNull(cut.Find("th[data-omni-col='EntryRoute'] .omni-data-grid__sort"));
        });
    }

    [Fact]
    public void Visitors_WithoutRecordedEntryPage_ShowsTheNotRecordedText()
    {
        _handler.SetJsonResponse(VisitorsUrl, new List<AppWebAnalyticsVisitorDto>
        {
            new() { Visitor = "cccc3333", FirstSeenAtUtc = Early, EntryRoute = null }
        });

        var cut = Render<AppWebAnalyticsVisitorsList>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() =>
            Assert.Contains("VisitorEntryPageUnknown", cut.FindAll(".omni-data-grid__table tbody tr[data-omni-row-index]")[0].TextContent));
    }

    [Fact]
    public void Visitors_HttpFailure_ShowsErrorAndRetry()
    {
        _handler.SetResponse(VisitorsUrl, HttpStatusCode.ServiceUnavailable);

        var cut = Render<AppWebAnalyticsVisitorsList>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("LoadFailed", cut.Markup);
            Assert.Contains("Retry", cut.Markup);
        });
    }

    [Fact]
    public void WebAnalyticsView_ListsTheVisitorsBelowTheSummary()
    {
        _handler.SetJsonResponse("api/appmonitoring/apps/5/web-analytics?days=30",
            new AppWebAnalyticsSummaryDto { UniqueVisitorsToday = 1 });
        _handler.SetJsonResponse(VisitorsUrl, new List<AppWebAnalyticsVisitorDto>
        {
            new() { Visitor = "aaaa1111", FirstSeenAtUtc = Early, EntryRoute = "/login" }
        });

        var cut = Render<AppWebAnalyticsView>(parameters => parameters.Add(component => component.AppId, 5));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("VisitorsListTitle", cut.Markup);
            Assert.Contains("/login", cut.Markup);
        });
    }
}
