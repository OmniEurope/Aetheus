// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Monitoring;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Monitoring;

/// <summary>
/// PLAN-003 lot 28: one page lists every supervised app, all projects together, with the last event
/// it sent, which is what distinguishes "collected, then stopped" from "never configured".
/// </summary>
public sealed class SupervisionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public SupervisionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        // PLAN-013 lot 5: the page also lists the storage of each project.
        _handler.SetJsonResponse("api/data-storage/projects", new ProjectStorageReportDto());
    }

    [Fact]
    public void ListsAppsOfEveryProject_WithTheirLastEvent_OrNever()
    {
        var lastEvent = new DateTime(2026, 8, 28, 21, 57, 0, DateTimeKind.Utc);
        _handler.SetJsonResponse("api/appmonitoring/summary", new AppMonitoringSummaryDto
        {
            TotalCount = 2,
            Applications =
            [
                new MonitoredAppStatusDto { Id = 3, Name = "Aetheus", ProjectId = 1, ProjectName = "Aetheus", CurrentStatus = AppHealthStatus.Up, LastEventAt = lastEvent },
                new MonitoredAppStatusDto { Id = 8, Name = "Orpheus web", ProjectId = 4, ProjectName = "Orpheus", CurrentStatus = AppHealthStatus.Unknown }
            ]
        });

        var cut = Render<Supervision>();

        cut.WaitForAssertion(() => Assert.Contains("Orpheus web", cut.Markup, StringComparison.Ordinal));
        // R-467: each application opens its project's supervision page on itself.
        Assert.Contains("href=\"/projects/1/monitoring?app=3\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/projects/4/monitoring?app=8\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(lastEvent.ToLocalTime().ToString("g"), cut.Markup, StringComparison.Ordinal);
        Assert.Contains("NeverReceived", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R468_TheTotalsOfEveryApplication_SitAboveTheList()
    {
        _handler.SetJsonResponse("api/appmonitoring/summary", new AppMonitoringSummaryDto
        {
            TotalCount = 3,
            UpCount = 2,
            DownCount = 1,
            Applications = [new MonitoredAppStatusDto { Id = 3, Name = "Aetheus", ProjectId = 1, CurrentStatus = AppHealthStatus.Up }],
            Audience = new AppAudienceTotalsDto
            {
                ApplicationCount = 2,
                OnlineVisitors = 7,
                VisitorsToday = 5,
                VisitorsThisWeek = 12,
                VisitorsThisMonth = 40,
                SessionsThisMonth = 90,
                PageViewsThisMonth = 600
            }
        });

        var cut = Render<Supervision>();

        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".supervision-totals > .omni-stat-tile").Count));
        var tiles = cut.FindAll(".supervision-totals > .omni-stat-tile")
            .ToDictionary(
                tile => tile.QuerySelector(".omni-stat-tile__label")!.TextContent.Trim(),
                tile => tile.QuerySelector(".omni-stat-tile__value")!.TextContent.Trim());
        Assert.Equal("3", tiles["MonitoredApps"]);
        Assert.Equal("7", tiles["CurrentVisitors"]);
        Assert.Equal("5", tiles["UniqueVisitorsToday"]);
        Assert.Equal("12", tiles["UniqueVisitorsThisWeek"]);
        Assert.Equal("40", tiles["UniqueVisitorsThisMonth"]);
        Assert.Equal("90", tiles["SessionsThisMonth"]);
        Assert.Equal("600", tiles["PageViewsThisMonth"]);
        // Two applications are added up: the page says a visitor of both counts in each.
        Assert.Contains("SupervisionAudienceNote", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R468_WithoutAnAudience_OnlyTheApplicationsAreCounted()
    {
        _handler.SetJsonResponse("api/appmonitoring/summary", new AppMonitoringSummaryDto
        {
            TotalCount = 1,
            Applications = [new MonitoredAppStatusDto { Id = 3, Name = "Aetheus", ProjectId = 1, CurrentStatus = AppHealthStatus.Up }]
        });

        var cut = Render<Supervision>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".supervision-totals > .omni-stat-tile")));
        Assert.DoesNotContain("SupervisionAudienceNote", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedLoad_SaysSo_InsteadOfAnEmptyList()
    {
        _handler.SetResponse(HttpMethod.Get, "api/appmonitoring/summary", HttpStatusCode.InternalServerError);

        var cut = Render<Supervision>();

        cut.WaitForAssertion(() => Assert.Contains("GridLoadFailed", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("NoMonitoredApps", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void PLAN013Lot6_ANonAdministrator_DoesNotSeeTheReclaimAction()
    {
        _handler.SetJsonResponse("api/appmonitoring/summary", new AppMonitoringSummaryDto());

        var cut = Render<Supervision>();

        cut.WaitForAssertion(() => Assert.Contains("ProjectStorage", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("ReclaimSpaceHelp", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("reclaim", StringComparison.Ordinal));
    }
}
