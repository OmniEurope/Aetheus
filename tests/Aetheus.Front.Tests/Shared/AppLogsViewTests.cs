// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The production Logs tab (2026-09-12) printed message templates, and a failed call looked exactly
/// like an app that never logged. These pin both, and the grid of recette R-358 (sort and header
/// filters sent to the API, live refresh on the telemetry push).
/// </summary>
public sealed class AppLogsViewTests : BunitContext
{
    private const string LogsUrl = "api/appmonitoring/apps/3/logs";

    [Fact]
    public void LogLines_ShowTheValuesNotTheTemplate()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto>
        {
            Items =
            [
                new AppLogEntryDto
                {
                    Id = 1,
                    Timestamp = new DateTime(2026, 9, 12, 12, 10, 40, DateTimeKind.Utc),
                    SeverityNumber = 9, SeverityText = "Information",
                    Body = "Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode}",
                    AttributesJson = "{\"ElapsedMilliseconds\":\"14.64\",\"StatusCode\":\"200\"}"
                }
            ],
            TotalCount = 1
        });

        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() =>
            Assert.Contains("Received HTTP response headers after 14.64ms - 200", cut.Markup, StringComparison.Ordinal));
        // Recette R-358: the rendered message is also the cell's hover text, in full.
        Assert.Contains(cut.FindAll(".app-log-cell-text"),
            cell => cell.GetAttribute("title") == "Received HTTP response headers after 14.64ms - 200");
        // Recette R2-012: the badge names the class of the severity number (the test localizer answers the key).
        Assert.Equal("LogSeverityInfo", cut.Find("td[data-omni-col='Severity'] .omni-badge").TextContent.Trim());
        Assert.DoesNotContain("NoLogsYet", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedCall_SaysSoInsteadOfNoLogsYet()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Get, LogsUrl, HttpStatusCode.InternalServerError);

        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("NoLogsYet", cut.Markup, StringComparison.Ordinal);
        // The Retry of a failed load stays; only the unconditional Refresh went away (R-181).
        Assert.Single(cut.FindAll("button"), b => b.TextContent.Contains("Retry", StringComparison.Ordinal));
    }

    [Fact]
    public void NoRecord_SaysNoLogsYet_WithoutTheFailureAlert()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = [], TotalCount = 0 });

        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Contains("NoLogsYet", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("LoadFailed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R358_TheGridAsksTheApiForTheSortedLog_WithTheColumnsOfTheRecord()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto>
        {
            Items = [new AppLogEntryDto { Id = 1, Timestamp = DateTime.UtcNow, SeverityNumber = 17, Body = "boom", AttributesJson = "{\"node\":\"db-1\"}" }],
            TotalCount = 1
        });

        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Contains("boom", cut.Markup, StringComparison.Ordinal));
        // Newest first unless the reader sorts a column: the order is the API's, over the whole log.
        Assert.Contains(handler.Requests, request =>
            request.Url.Contains(LogsUrl, StringComparison.Ordinal)
            && request.Url.Contains("sortBy=Timestamp", StringComparison.Ordinal)
            && request.Url.Contains("sortDescending=true", StringComparison.Ordinal));
        foreach (var title in new[] { "Timestamp", "Severity", "Message", "LogAttributes" })
            Assert.Contains(title, cut.Markup, StringComparison.Ordinal);
        Assert.Contains("{\"node\":\"db-1\"}", cut.Find(".app-log-attributes").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R181_NoRefreshButton_ATelemetryPushOfTheProjectReloadsTheLines()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto>
        {
            Items = [new AppLogEntryDto { Id = 1, Timestamp = DateTime.UtcNow, SeverityNumber = 9, Body = "first line" }],
            TotalCount = 1
        });
        var cut = Render<AppLogsView>(parameters => parameters
            .Add(component => component.AppId, 3)
            .Add(component => component.ProjectId, 8));
        cut.WaitForAssertion(() => Assert.Contains("first line", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Refresh", StringComparison.Ordinal));
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto>
        {
            Items = [new AppLogEntryDto { Id = 2, Timestamp = DateTime.UtcNow, SeverityNumber = 17, Body = "pushed line" }],
            TotalCount = 1
        });

        await cut.Instance.LiveFeed!.OnEventAsync(8);

        cut.WaitForAssertion(() => Assert.Contains("pushed line", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task R181_APushWhoseCallFails_KeepsTheLinesOnScreen()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto>
        {
            Items = [new AppLogEntryDto { Id = 1, Timestamp = DateTime.UtcNow, SeverityNumber = 9, Body = "first line" }],
            TotalCount = 1
        });
        var cut = Render<AppLogsView>(parameters => parameters
            .Add(component => component.AppId, 3)
            .Add(component => component.ProjectId, 8));
        cut.WaitForAssertion(() => Assert.Contains("first line", cut.Markup, StringComparison.Ordinal));
        handler.SetResponse(HttpMethod.Get, LogsUrl, HttpStatusCode.InternalServerError);
        var requestsBefore = handler.Requests.Count;

        await cut.Instance.LiveFeed!.OnEventAsync(8);

        cut.WaitForAssertion(() => Assert.True(handler.Requests.Count > requestsBefore));
        Assert.Contains("first line", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadFailed", cut.Markup, StringComparison.Ordinal);
    }

    private static List<AppLogEntryDto> Lines(int firstId, int count) =>
        [.. Enumerable.Range(firstId, count).Select(id => new AppLogEntryDto
        {
            Id = id, Timestamp = DateTime.UtcNow, SeverityNumber = 17, Body = $"line {id}"
        })];

    /// <summary>The grid shows one line of 201; the export reads the two pages of 200 the API serves.</summary>
    private static BunitTestHelper.TestHandler ArrangeTwoExportPages(BunitContext context)
    {
        var handler = BunitTestHelper.RegisterServices(context);
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = Lines(1, 1), TotalCount = 201 });
        handler.SetJsonResponse($"{LogsUrl}?hours=24&page=1&pageSize=200",
            new PaginatedResult<AppLogEntryDto> { Items = Lines(1, 200), TotalCount = 201 });
        handler.SetJsonResponse($"{LogsUrl}?hours=24&page=2&pageSize=200",
            new PaginatedResult<AppLogEntryDto> { Items = Lines(201, 1), TotalCount = 201 });
        return handler;
    }

    /// <summary>The file goes to the browser through the OE download helper module.</summary>
    private BunitJSModuleInterop DownloadModule()
    {
        var module = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omni-document-editor.js");
        module.SetupVoid("download", _ => true).SetVoidResult();
        return module;
    }

    /// <summary>Recette R2-011: the Markdown button of the grid's export bar (Markdown, then CSV).</summary>
    private static AngleSharp.Dom.IElement MarkdownExportButton(IRenderedComponent<AppLogsView> cut) =>
        cut.FindAll(".omni-data-grid__export-button")[0];

    [Fact]
    public void R2011_NoLevelDropdownNorSearchBoxNorButtonAboveTheGrid_TheExportBarIsUnderIt()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = Lines(1, 1), TotalCount = 1 });

        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Contains("line 1", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("#app-logs-level"));
        Assert.Empty(cut.FindAll("#app-logs-search"));
        Assert.Empty(cut.FindComponents<OmniMarkdownExportButton<AppLogEntryDto>>());
        Assert.Single(cut.FindAll(".omni-data-grid__export-button"));
        // Under the table: the bar follows the grid's table in the markup.
        var markup = cut.Markup;
        Assert.True(markup.IndexOf("omni-data-grid__export", StringComparison.Ordinal)
                    > markup.IndexOf("<table", StringComparison.Ordinal));
        // The grid's requests no longer carry a level or a search.
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("minSeverity", StringComparison.Ordinal)
            || request.Url.Contains("search=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R2011_Export_PagesThroughTheApi_AndDownloadsEveryLineAsMarkdown_UnderAReadableName()
    {
        var handler = ArrangeTwoExportPages(this);
        var module = DownloadModule();
        var cut = Render<AppLogsView>(parameters => parameters
            .Add(component => component.AppId, 3)
            .Add(component => component.AppName, "Aetheus"));
        cut.WaitForAssertion(() => Assert.Contains("line 1", cut.Markup, StringComparison.Ordinal));

        await MarkdownExportButton(cut).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        var download = module.Invocations["download"].Single();
        var fileName = Assert.IsType<string>(download.Arguments[0]);
        var markdown = System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(download.Arguments[2]));
        // Recette R2-036: the application's name, then the content; OE appends its generation time once.
        Assert.Matches(@"^aetheus-logs-\d{4}-\d{2}-\d{2}-\d{4}\.md$", fileName);
        // Both pages, with the grid's sort, and every line once.
        Assert.Contains(handler.Requests, request => request.Url.Contains("page=1&pageSize=200", StringComparison.Ordinal)
            && request.Url.Contains("sortBy=Timestamp&sortDescending=true", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Url.Contains("page=2&pageSize=200", StringComparison.Ordinal));
        Assert.Contains("LogsExportTitle", markdown, StringComparison.Ordinal);
        Assert.Contains("Aetheus (#3)", markdown, StringComparison.Ordinal);
        Assert.Contains("LogsExportPeriodValue", markdown, StringComparison.Ordinal);
        Assert.Contains("LogSeverityError (17)", markdown, StringComparison.Ordinal);
        Assert.Contains("line 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("line 201 |", markdown, StringComparison.Ordinal);
        Assert.Equal(201, markdown.Split('\n').Count(line => line.Contains("LogSeverityError (17)", StringComparison.Ordinal)));
        // Every announced line came back, so the file carries no truncation notice.
        Assert.DoesNotContain("\n> ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R2011_Export_APageThatFails_DownloadsNoFile_AndSaysSo()
    {
        var handler = ArrangeTwoExportPages(this);
        handler.SetResponse(HttpMethod.Get, $"{LogsUrl}?hours=24&page=2&pageSize=200", HttpStatusCode.InternalServerError);
        var module = DownloadModule();
        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));
        cut.WaitForAssertion(() => Assert.Contains("line 1", cut.Markup, StringComparison.Ordinal));

        await MarkdownExportButton(cut).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Summary == "LogsExportFailed"));
        Assert.Empty(module.Invocations["download"]);
    }

    [Fact]
    public void R2012_TheSeverityFilter_ListsTheLevelsByGravity_UnderTheNameTheBadgeShows()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto>
        {
            // The exporter spelled the level "Warning"; the filter names its class WARN.
            Items = [new AppLogEntryDto { Id = 1, Timestamp = DateTime.UtcNow, SeverityNumber = 13, SeverityText = "Warning", Body = "disk almost full" }],
            TotalCount = 1
        });

        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Contains("disk almost full", cut.Markup, StringComparison.Ordinal));
        // OE 1.4.0 keeps the declared order (it sorted alphabetically: DEBUG, ERROR, FATAL, INFO...).
        var options = cut.FindAll("th[data-omni-col='Severity'] .omni-multi-select__option")
            .Select(option => option.TextContent.Trim()).ToArray();
        Assert.Equal(
            ["LogSeverityTrace", "LogSeverityDebug", "LogSeverityInfo", "LogSeverityWarn", "LogSeverityError", "LogSeverityFatal"],
            options);
        // The badge of the line and the filter say the same word, not the raw SeverityText.
        var badge = cut.Find("td[data-omni-col='Severity'] .omni-badge").TextContent.Trim();
        Assert.Equal("LogSeverityWarn", badge);
        Assert.Contains(badge, options);
    }

    [Fact]
    public void R2012_TheFilter_StillSendsTheClassName_OnlyItsDisplayIsLocalized()
    {
        BunitTestHelper.RegisterServices(this).SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = Lines(1, 1), TotalCount = 1 });

        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Contains("line 1", cut.Markup, StringComparison.Ordinal));
        var severity = Assert.Single(cut.FindComponents<OmniDataGridColumn<AppLogEntryDto>>(),
            column => column.Instance.Key == "Severity");
        Assert.Equal(["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"], severity.Instance.FilterValues!);
        Assert.Equal("LogSeverityWarn", severity.Instance.FormatFilterValue!("WARN"));
    }

    [Fact]
    public void R2009_TheExportBar_IsOneBlueMarkdownButtonWithTheMdIcon()
    {
        BunitTestHelper.RegisterServices(this).SetJsonResponse(LogsUrl, new PaginatedResult<AppLogEntryDto> { Items = Lines(1, 1), TotalCount = 1 });

        var cut = Render<AppLogsView>(parameters => parameters.Add(component => component.AppId, 3));

        cut.WaitForAssertion(() => Assert.Contains("line 1", cut.Markup, StringComparison.Ordinal));
        var buttons = cut.FindComponents<OmniButton>()
            .Where(button => button.Instance.Class?.Contains("omni-data-grid__export-button", StringComparison.Ordinal) == true)
            .ToArray();
        // One blue button in the bar, the user's exception (R2-009): Markdown, with the file icon of its format;
        // no CSV (R2-124, second decision of 2026-10-05).
        var button = Assert.Single(buttons);
        Assert.Equal(OmniButtonVariant.Primary, button.Instance.Variant);
        Assert.Equal(OmniIconName.FileMd, button.FindComponent<OmniIcon>().Instance.Name);
        Assert.Single(cut.FindAll(".omni-data-grid__export .omni-button--primary"));
    }
}
