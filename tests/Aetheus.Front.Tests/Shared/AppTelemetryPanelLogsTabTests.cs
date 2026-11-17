// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-34: the Journaux sub-tab of the project Supervision page, opened from the telemetry panel (not the view
/// on its own): opening the tab, and coming back to it from another sub-tab, shows a grid with a correct state for a
/// read that succeeds empty, a read that succeeds with lines and a read that really fails.</summary>
public sealed class AppTelemetryPanelLogsTabTests : BunitContext
{
    private const string LogsUrl = "api/appmonitoring/apps/5/logs";
    private readonly BunitTestHelper.TestHandler _handler;

    public AppTelemetryPanelLogsTabTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "/probe-now", new AppProbeNowDto());
        _handler.SetJsonResponse(HttpMethod.Get, "/telemetry-export-request", new AppTelemetryExportRequestDto());
        _handler.SetJsonResponse("api/appmonitoring/projects/1/apps", new List<MonitoredAppDto>
        {
            new() { Id = 5, ProjectId = 1, Name = "toto-qa", CurrentStatus = AppHealthStatus.Up, HasIngestKey = true }
        });
        _handler.SetJsonResponse("/send-interval", new AppTelemetrySendIntervalDto
        {
            ServerIntervalSeconds = 300,
            Profile = DataRetentionProfile.Standard,
            ObservedIntervalSeconds = 60
        });
        _handler.SetJsonResponse("/errors", new PaginatedResult<AppErrorEventDto>());
        _handler.SetJsonResponse("/errors/exception-types", new List<string>());
        _handler.SetJsonResponse("/web-analytics?days=30", new AppWebAnalyticsSummaryDto());
        _handler.SetJsonResponse("web-analytics/configuration", new AppWebAnalyticsConfigurationDto { SiteId = "toto-qa" });
    }

    private IRenderedComponent<AppTelemetryPanel> OpenPanel()
    {
        var cut = Render<AppTelemetryPanel>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='tab']")));
        return cut;
    }

    private static void SelectTab(IRenderedComponent<AppTelemetryPanel> cut, string title) =>
        cut.FindAll("[role='tab']").Single(tab => tab.GetAttribute("data-key") == title).Click();

    /// <summary>The panel section the tab controls: shown when selected, hidden otherwise.</summary>
    private static AngleSharp.Dom.IElement PanelOf(IRenderedComponent<AppTelemetryPanel> cut, string title)
    {
        var panelId = cut.FindAll("[role='tab']").Single(tab => tab.GetAttribute("data-key") == title).GetAttribute("aria-controls");
        return cut.Find($"section[id='{panelId}']");
    }

    private static List<AppLogEntryDto> Lines(int count) =>
        [.. Enumerable.Range(1, count).Select(id => new AppLogEntryDto
        {
            Id = id, Timestamp = DateTime.UtcNow, SeverityNumber = 9, Body = $"log line {id}"
        })];

    private int LogReads => _handler.Requests.Count(request => request.Url.Contains(LogsUrl, StringComparison.Ordinal));

    [Fact]
    public void OpeningJournaux_AfterASuccessWithoutLines_DrawsTheGridAndSaysThereIsNoLog()
    {
        _handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = [], TotalCount = 0 });
        var cut = OpenPanel();

        SelectTab(cut, "Logs");

        cut.WaitForAssertion(() =>
        {
            var panel = PanelOf(cut, "Logs");
            Assert.False(panel.HasAttribute("hidden"));
            Assert.Contains("NoLogsYet", panel.TextContent, StringComparison.Ordinal);
            Assert.NotEmpty(panel.QuerySelectorAll("table.omni-data-grid__table"));
            Assert.DoesNotContain("LoadFailed", panel.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OpeningJournaux_AfterASuccessWithLines_ShowsTheLines()
    {
        _handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = Lines(3), TotalCount = 3 });
        var cut = OpenPanel();

        SelectTab(cut, "Logs");

        cut.WaitForAssertion(() =>
        {
            var panel = PanelOf(cut, "Logs");
            Assert.Contains("log line 1", panel.TextContent, StringComparison.Ordinal);
            Assert.Contains("log line 3", panel.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("NoLogsYet", panel.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("LoadFailed", panel.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OpeningJournaux_AfterARealReadFailure_SaysSoWithRetry_NeverNoLogsYet()
    {
        _handler.SetResponse(HttpMethod.Get, LogsUrl, HttpStatusCode.InternalServerError);
        var cut = OpenPanel();

        SelectTab(cut, "Logs");

        cut.WaitForAssertion(() =>
        {
            var panel = PanelOf(cut, "Logs");
            Assert.Contains("LoadFailed", panel.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("NoLogsYet", panel.TextContent, StringComparison.Ordinal);
            Assert.NotEmpty(panel.QuerySelectorAll("table.omni-data-grid__table"));
            Assert.Single(panel.QuerySelectorAll("button"), b => b.TextContent.Contains("Retry", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ReturningToJournaux_KeepsTheLinesOnScreen_AndDoesNotReadAgainByItself()
    {
        _handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = Lines(2), TotalCount = 2 });
        var cut = OpenPanel();
        SelectTab(cut, "Logs");
        cut.WaitForAssertion(() => Assert.Contains("log line 2", PanelOf(cut, "Logs").TextContent, StringComparison.Ordinal));
        var readsBefore = LogReads;

        SelectTab(cut, "Errors");
        cut.WaitForAssertion(() => Assert.True(PanelOf(cut, "Logs").HasAttribute("hidden")));
        SelectTab(cut, "Logs");

        cut.WaitForAssertion(() =>
        {
            var panel = PanelOf(cut, "Logs");
            Assert.False(panel.HasAttribute("hidden"));
            Assert.Contains("log line 1", panel.TextContent, StringComparison.Ordinal);
            Assert.Contains("log line 2", panel.TextContent, StringComparison.Ordinal);
            Assert.NotEmpty(panel.QuerySelectorAll("table.omni-data-grid__table"));
        });
        Assert.Equal(readsBefore, LogReads);
    }

    [Fact]
    public void Retry_AfterAFailure_ReadsAgain_AndShowsTheLines_WithoutLeavingTheTab()
    {
        _handler.SetResponse(HttpMethod.Get, LogsUrl, HttpStatusCode.InternalServerError);
        var cut = OpenPanel();
        SelectTab(cut, "Logs");
        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", PanelOf(cut, "Logs").TextContent, StringComparison.Ordinal));
        var readsBefore = LogReads;

        _handler.SetJsonResponse(HttpMethod.Get, LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = Lines(1), TotalCount = 1 });
        PanelOf(cut, "Logs").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Retry", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.True(LogReads > readsBefore));
        cut.WaitForAssertion(() =>
        {
            var panel = PanelOf(cut, "Logs");
            Assert.Contains("log line 1", panel.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("LoadFailed", panel.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ReturningToJournaux_AfterAFailure_StillSaysTheReadFailed_AndRetryReadsAgain()
    {
        _handler.SetResponse(HttpMethod.Get, LogsUrl, HttpStatusCode.InternalServerError);
        var cut = OpenPanel();
        SelectTab(cut, "Logs");
        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", PanelOf(cut, "Logs").TextContent, StringComparison.Ordinal));

        SelectTab(cut, "Errors");
        SelectTab(cut, "Logs");

        cut.WaitForAssertion(() =>
        {
            var panel = PanelOf(cut, "Logs");
            Assert.False(panel.HasAttribute("hidden"));
            Assert.Contains("LoadFailed", panel.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("NoLogsYet", panel.TextContent, StringComparison.Ordinal);
        });

        _handler.SetJsonResponse(HttpMethod.Get, LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = Lines(1), TotalCount = 1 });
        PanelOf(cut, "Logs").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Retry", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            var panel = PanelOf(cut, "Logs");
            Assert.Contains("log line 1", panel.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("LoadFailed", panel.TextContent, StringComparison.Ordinal);
        });
    }
}
