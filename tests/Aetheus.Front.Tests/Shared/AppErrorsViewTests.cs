// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

public sealed class AppErrorsViewTests : BunitContext
{
    private const string TypesUrl = "api/appmonitoring/apps/7/errors/exception-types";

    /// <summary>Registers the services and the exception-type list the type filter reads.</summary>
    private BunitTestHelper.TestHandler Arrange(params string[] exceptionTypes)
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetJsonResponse(TypesUrl, exceptionTypes.ToList());
        return handler;
    }

    [Fact]
    public void TheGridReadsTheSortedGroupsFromTheApi_ByBlocks()
    {
        var handler = Arrange("InvalidOperationException");
        handler.SetJsonResponse(ErrorsUrl, new PaginatedResult<AppErrorEventDto>
        {
            Items = [new AppErrorEventDto { Id = 1, ExceptionType = "InvalidOperationException", Message = "failure" }],
            TotalCount = 30,
            Page = 1,
            PageSize = 25
        });

        var cut = Render<AppErrorsView>(parameters => parameters.Add(component => component.AppId, 7));

        cut.WaitForAssertion(() => Assert.Contains("failure", cut.Markup, StringComparison.Ordinal));
        // Remote virtualization wiring (the test harness turns the virtualization itself off): the grid
        // loads its blocks from the API through LoadData and reads them back through VirtualData.
        var grid = cut.FindComponent<AetheusDataGrid<AppErrorEventDto>>().Instance;
        Assert.True(grid.LoadData.HasDelegate);
        Assert.NotNull(grid.VirtualData);
        Assert.True(grid.FullHeight);
        // Most recently seen first unless the reader sorts a column: the order is the API's.
        Assert.Contains(handler.Requests, request =>
            request.Url.Contains($"{ErrorsUrl}?page=1&pageSize=25", StringComparison.Ordinal)
            && request.Url.Contains("sortBy=LastSeenAt&sortDescending=true", StringComparison.Ordinal));
        // The type filter's candidates are every type of the app, read from the API.
        Assert.Contains(handler.Requests, request => request.Url.Contains(TypesUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheHeaderFiltersAndTheSort_TravelToTheApi_NotAppliedToTheLoadedRows()
    {
        var handler = Arrange("InvalidOperationException", "TimeoutException");
        handler.SetJsonResponse(ErrorsUrl, new PaginatedResult<AppErrorEventDto> { Items = Groups(1, 3), TotalCount = 3 });
        var cut = Render<AppErrorsView>(parameters => parameters.Add(component => component.AppId, 7));
        cut.WaitForAssertion(() => Assert.Contains("group 1", cut.Markup, StringComparison.Ordinal));
        var grid = cut.FindComponent<AetheusDataGrid<AppErrorEventDto>>();

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(FilteredLoad()));

        cut.WaitForAssertion(() => Assert.Contains(handler.Requests, request => IsFilteredQuery(request.Url, "page=1&pageSize=25")));
    }

    [Fact]
    public async Task R2011_Export_ReadsWithTheGridsOwnColumnFilters()
    {
        var handler = ArrangeTwoExportPages(this);
        var module = DownloadModule();
        var cut = Render<AppErrorsView>(parameters => parameters.Add(component => component.AppId, 7));
        cut.WaitForAssertion(() => Assert.Contains("group 1", cut.Markup, StringComparison.Ordinal));
        var grid = cut.FindComponent<AetheusDataGrid<AppErrorEventDto>>().Instance.Grid!;
        await cut.InvokeAsync(() => grid.SetFiltersAsync(
            new Dictionary<string, string?> { [nameof(AppErrorEventDto.Message)] = "group" }, replace: true));

        await MarkdownExportButton(cut).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        // The export asks the API with the filter the grid holds, not with the rows it shows.
        Assert.Contains(handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains($"{ErrorsUrl}?page=1&pageSize=200", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Message", StringComparison.Ordinal)
                && url.Contains("Filters[0].Value=group", StringComparison.Ordinal);
        });
    }

    /// <summary>What the grid sends once the reader ticks one type, asks for 5 occurrences or more and
    /// sorts by count, ascending.</summary>
    private static GridLoadArgs FilteredLoad() => new()
    {
        Skip = 0,
        Top = 25,
        OrderBy = "OccurrenceCount asc",
        Filters =
        [
            new GridFilterDescriptor(nameof(AppErrorEventDto.ExceptionType), "TimeoutException", OmniDataGridFilterOperator.In),
            new GridFilterDescriptor(nameof(AppErrorEventDto.OccurrenceCount), "5", OmniDataGridFilterOperator.GreaterThanOrEquals)
        ]
    };

    private static bool IsFilteredQuery(string requestUrl, string paging)
    {
        var url = Uri.UnescapeDataString(requestUrl);
        return url.Contains($"{ErrorsUrl}?{paging}", StringComparison.Ordinal)
            && url.Contains("sortBy=OccurrenceCount&sortDescending=false", StringComparison.Ordinal)
            && url.Contains("Filters[0].Field=ExceptionType", StringComparison.Ordinal)
            && url.Contains("Filters[0].Value=TimeoutException", StringComparison.Ordinal)
            && url.Contains("Filters[1].Field=OccurrenceCount", StringComparison.Ordinal)
            && url.Contains("Filters[1].Value=5", StringComparison.Ordinal);
    }

    [Fact]
    public void R361_ClickingAnErrorRow_OpensItsDetailDialog_AndLeavesTheGridAsItWas()
    {
        var handler = Arrange("InvalidOperationException");
        BunitTestHelper.UseImmediateDialogs(this);
        var error = new AppErrorEventDto
        {
            Id = 4,
            ExceptionType = "InvalidOperationException",
            Message = "boom",
            TopFrame = "at Toto.Run()",
            OccurrenceCount = 3
        };
        handler.SetJsonResponse("api/appmonitoring/apps/7/errors", new PaginatedResult<AppErrorEventDto>
        {
            Items = [error],
            TotalCount = 1
        });
        var cut = Render<AppErrorsView>(parameters => parameters.Add(component => component.AppId, 7));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tr[data-omni-row-index='0']")));
        var errorFetchesBefore = handler.Requests.Count(request => request.Url.Contains("/errors", StringComparison.Ordinal));

        cut.Find("tr[data-omni-row-index='0']").Click();

        var dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.Equal(1, dialogs.OpenCount);
        Assert.Equal(typeof(AppErrorDetailDialog), dialogs.LastComponent);
        Assert.Equal("ErrorDetails", dialogs.LastTitle);
        var passed = Assert.IsType<AppErrorEventDto>(dialogs.LastParameters![nameof(AppErrorDetailDialog.Error)]);
        Assert.Equal(4, passed.Id);
        // The dialog closed (immediately here): the grid keeps its row and nothing was re-fetched.
        Assert.Single(cut.FindAll("tr[data-omni-row-index]"));
        Assert.Contains("InvalidOperationException", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(errorFetchesBefore, handler.Requests.Count(request => request.Url.Contains("/errors", StringComparison.Ordinal)));
    }

    [Fact]
    public void R361_DetailDialog_ShowsEveryFieldOfTheGroup_AndClosesOnClose()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
        var cut = Render<AppErrorDetailDialog>(parameters => parameters.Add(component => component.Error, new AppErrorEventDto
        {
            Id = 4,
            Fingerprint = "fp-123",
            ExceptionType = "InvalidOperationException",
            Message = "boom happened",
            TopFrame = "at Toto.Run() in Toto.cs:line 12",
            OccurrenceCount = 3,
            FirstSeenAt = new DateTime(2026, 9, 20, 8, 0, 0),
            LastSeenAt = new DateTime(2026, 9, 25, 9, 30, 0)
        }));

        Assert.Contains("InvalidOperationException", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("boom happened", cut.Find(".app-error-detail-message").TextContent, StringComparison.Ordinal);
        Assert.Contains("at Toto.Run() in Toto.cs:line 12", cut.Find(".app-error-detail-frame").TextContent, StringComparison.Ordinal);
        Assert.Contains("fp-123", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("dd"), dd => dd.TextContent.Trim() == "3");
        Assert.Contains(new DateTime(2026, 9, 20, 8, 0, 0).ToString("g", System.Globalization.CultureInfo.CurrentCulture), cut.Markup, StringComparison.Ordinal);
        Assert.Contains(new DateTime(2026, 9, 25, 9, 30, 0).ToString("g", System.Globalization.CultureInfo.CurrentCulture), cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".app-error-detail-no-frame"));

        cut.FindAll("button").Single(button => button.TextContent.Contains("Close", StringComparison.Ordinal)).Click();

        var spy = (SpyDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.True(spy.Closed);
    }

    [Fact]
    public void R361_DetailDialog_WithoutTopFrame_SaysSoInsteadOfAnEmptyTrace()
    {
        BunitTestHelper.RegisterServices(this);
        var cut = Render<AppErrorDetailDialog>(parameters => parameters.Add(component => component.Error, new AppErrorEventDto
        {
            Id = 5,
            ExceptionType = "TimeoutException",
            Message = "slow"
        }));

        Assert.Contains("ErrorNoTopFrame", cut.Find(".app-error-detail-no-frame").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".app-error-detail-frame"));
    }

    [Fact]
    public async Task R181_NoRefreshButton_ATelemetryPushOfTheProjectReloadsTheGrid()
    {
        var handler = Arrange("InvalidOperationException");
        handler.SetJsonResponse("api/appmonitoring/apps/7/errors", new PaginatedResult<AppErrorEventDto>
        {
            Items = [new AppErrorEventDto { Id = 1, ExceptionType = "InvalidOperationException", Message = "first" }],
            TotalCount = 1
        });
        var cut = Render<AppErrorsView>(parameters => parameters
            .Add(component => component.AppId, 7)
            .Add(component => component.ProjectId, 3));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Refresh", StringComparison.Ordinal));
        handler.SetJsonResponse("api/appmonitoring/apps/7/errors", new PaginatedResult<AppErrorEventDto>
        {
            Items = [new AppErrorEventDto { Id = 2, ExceptionType = "TimeoutException", Message = "pushed" }],
            TotalCount = 1
        });

        await cut.Instance.LiveFeed!.OnEventAsync(4);
        Assert.DoesNotContain("TimeoutException", cut.Markup, StringComparison.Ordinal);

        await cut.Instance.LiveFeed.OnEventAsync(3);
        cut.WaitForAssertion(() => Assert.Contains("TimeoutException", cut.Markup, StringComparison.Ordinal));
    }

    private const string ErrorsUrl = "api/appmonitoring/apps/7/errors";

    private static List<AppErrorEventDto> Groups(int firstId, int count) =>
        [.. Enumerable.Range(firstId, count).Select(id => new AppErrorEventDto
        {
            Id = id, ExceptionType = "InvalidOperationException", Message = $"group {id}", OccurrenceCount = 1,
            FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow
        })];

    /// <summary>The grid shows the first page of 25 of 230 groups; the export reads the two pages of 200 the API serves.</summary>
    private static BunitTestHelper.TestHandler ArrangeTwoExportPages(BunitContext context)
    {
        var handler = BunitTestHelper.RegisterServices(context);
        handler.SetJsonResponse(TypesUrl, new List<string> { "InvalidOperationException" });
        handler.SetJsonResponse(ErrorsUrl, new PaginatedResult<AppErrorEventDto> { Items = Groups(1, 25), TotalCount = 230 });
        handler.SetJsonResponse($"{ErrorsUrl}?page=1&pageSize=200", new PaginatedResult<AppErrorEventDto> { Items = Groups(1, 200), TotalCount = 230 });
        handler.SetJsonResponse($"{ErrorsUrl}?page=2&pageSize=200", new PaginatedResult<AppErrorEventDto> { Items = Groups(201, 30), TotalCount = 230 });
        return handler;
    }

    private BunitJSModuleInterop DownloadModule()
    {
        var module = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omni-document-editor.js");
        module.SetupVoid("download", _ => true).SetVoidResult();
        return module;
    }

    /// <summary>The Markdown button of the grid's export bar (Markdown, then CSV).</summary>
    private static AngleSharp.Dom.IElement MarkdownExportButton(IRenderedComponent<AppErrorsView> cut) =>
        cut.FindAll(".omni-data-grid__export-button")[0];

    [Fact]
    public async Task R2011_Export_ReadsEveryErrorGroup_NotThePageOnScreen_FromTheBarUnderTheGrid()
    {
        var handler = ArrangeTwoExportPages(this);
        var module = DownloadModule();
        var cut = Render<AppErrorsView>(parameters => parameters.Add(component => component.AppId, 7).Add(component => component.AppName, "Shop"));
        cut.WaitForAssertion(() => Assert.Contains("group 1", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindComponents<OmniMarkdownExportButton<AppErrorEventDto>>());
        // Markdown is the bar's one blue button, CSV stays Ghost.
        Assert.Contains("omni-button--primary", MarkdownExportButton(cut).ClassList);
        Assert.Single(cut.FindAll(".omni-data-grid__export .omni-button--primary"));

        await MarkdownExportButton(cut).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        var download = module.Invocations["download"].Single();
        // The application's name, then the content; OE appends its generation time once.
        Assert.Matches(@"^shop-errors-\d{4}-\d{2}-\d{2}-\d{4}\.md$", Assert.IsType<string>(download.Arguments[0]));
        var markdown = System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(download.Arguments[2]));
        Assert.Contains(handler.Requests, request => request.Url.Contains("errors?page=1&pageSize=200", StringComparison.Ordinal)
            && request.Url.Contains("sortBy=LastSeenAt&sortDescending=true", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Url.Contains("errors?page=2&pageSize=200", StringComparison.Ordinal));
        Assert.Contains("ErrorsExportTitle", markdown, StringComparison.Ordinal);
        Assert.Contains("Shop (#7)", markdown, StringComparison.Ordinal);
        Assert.Contains("ErrorsExportPeriodValue", markdown, StringComparison.Ordinal);
        Assert.Contains("| group 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| group 230 |", markdown, StringComparison.Ordinal);
        Assert.Equal(230, markdown.Split('\n').Count(line => line.StartsWith("| InvalidOperationException", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task R2011_Export_APageThatFails_DownloadsNoFile_AndSaysSo()
    {
        var handler = ArrangeTwoExportPages(this);
        handler.SetResponse(HttpMethod.Get, $"{ErrorsUrl}?page=2&pageSize=200", System.Net.HttpStatusCode.InternalServerError);
        var module = DownloadModule();
        var cut = Render<AppErrorsView>(parameters => parameters.Add(component => component.AppId, 7));
        cut.WaitForAssertion(() => Assert.Contains("group 1", cut.Markup, StringComparison.Ordinal));

        await MarkdownExportButton(cut).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Summary == "ErrorsExportFailed"));
        Assert.Empty(module.Invocations["download"]);
    }
}
