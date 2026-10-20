// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using AlertsPage = Aetheus.Front.Components.Alerts.Alerts;
using LogsPage = Aetheus.Front.Components.Logs.Logs;
using ProjectsPage = Aetheus.Front.Components.Projects.Projects;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Covers live-log layout, recent-alert actions and the supervision entry.</summary>
public sealed class RecetteDialogsAndPagesTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public RecetteDialogsAndPagesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void LiveLogs_TitleAlone_TheTaskFieldAndWatchSitUnderTheHeader_WithTheExplanation()
    {
        var cut = Render<LogsPage>();

        // The header keeps the title; the task field and Watch are page content under it.
        var header = cut.Find(".omni-page-header__row");
        Assert.Empty(header.QuerySelectorAll("input"));
        Assert.DoesNotContain(header.QuerySelectorAll("button"), b => b.TextContent.Contains("Watch"));

        var watchRow = cut.Find(".live-logs-watch");
        Assert.NotNull(watchRow.QuerySelector("input#live-logs-task-id"));
        Assert.Contains("TaskId", watchRow.QuerySelector("label")!.TextContent);
        var watch = watchRow.QuerySelectorAll("button").Single(b => b.TextContent.Contains("Watch"));
        Assert.Contains("omni-button--primary", watch.ClassList);
        Assert.False(watch.HasAttribute("disabled"));
        Assert.Contains("LiveLogsDescription", cut.Markup);
    }

    [Fact]
    public void RecentAlerts_ClearIsAMenuAction_NamedAfterWhatItDoes()
    {
        _handler.SetJsonResponse("api/alerts", new List<AlertRuleDto>());
        var alerts = Services.GetRequiredService<AlertNotificationService>();
        var buffer = (List<AlertTriggeredDto>)typeof(AlertNotificationService)
            .GetField("_recent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(alerts)!;
        buffer.Add(new AlertTriggeredDto { RuleName = "CPU", Severity = "Critical", Message = "cpu high", TriggeredAt = DateTime.Now });

        var cut = Render<AlertsPage>();
        cut.WaitForState(() => cut.Markup.Contains("cpu high"));

        // No truncated "Tout marquer" button beside the heading; the action is in the menu.
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("MarkAllRead"));
        cut.Find(".omni-overflow-menu__trigger").Click();
        cut.FindAll("button.omni-menu__item").Single(b => b.TextContent.Contains("ClearRecentAlerts")).Click();

        Assert.Empty(alerts.Recent);
        cut.WaitForAssertion(() => Assert.DoesNotContain("cpu high", cut.Markup));
    }

    [Fact]
    public void Projects_HasNoSupervisionButton()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });

        var cut = Render<ProjectsPage>();

        // The all-projects supervision is a side-menu entry, not a button of the list.
        Assert.Empty(cut.FindAll(".project-list-supervision"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Supervision"));
    }

    [Fact]
    public void SideMenu_OffersTheSupervisionOfEveryProject_InTheProjectsGroup()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("monitoring");

        var cut = Render<NavMenu>();

        Assert.Contains("href=\"monitoring\"", cut.Markup);
        Assert.Contains("Supervision", cut.Markup);
    }
}
