// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerLogsSectionTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerLogsSectionTests() => _handler = BunitTestHelper.RegisterServices(this);

    private PaginatedResult<TaskLogDto> MakeLogPage(params TaskLogDto[] items) =>
        new() { Items = [.. items], TotalCount = items.Length };

    private IRenderedComponent<ServerLogsSection> RenderSection(int serverId = 1)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/logs", MakeLogPage(
            new TaskLogDto { Id = 1, Message = "Build started", Level = TaskLogLevel.Info },
            new TaskLogDto { Id = 2, Message = "Build failed", Level = TaskLogLevel.Error }
        ));
        return Render<ServerLogsSection>(p => p.Add(x => x.ServerId, serverId));
    }

    [Fact]
    public void Renders_WithLogs_OnInit()
    {
        var cut = RenderSection();
        cut.WaitForState(() => cut.Markup.Contains("Build started"));
        // OnInit GETs the first log page and renders the stubbed messages.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers/1/logs"));
        Assert.Contains("Build failed", cut.Markup);
    }

    [Fact]
    public void ServerIdChange_ReloadsLogsOnSameInstance()
    {
        _handler.SetJsonResponse("api/servers/1/logs", MakeLogPage(
            new TaskLogDto { Id = 1, Message = "first-log", Level = TaskLogLevel.Info }));
        _handler.SetJsonResponse("api/servers/2/logs", MakeLogPage(
            new TaskLogDto { Id = 2, Message = "second-log", Level = TaskLogLevel.Info }));
        var cut = Render<ServerLogsSection>(p => p.Add(x => x.ServerId, 1));

        cut.Render(p => p.Add(x => x.ServerId, 2));

        cut.WaitForAssertion(() => Assert.Contains("second-log", cut.Markup));
        Assert.DoesNotContain("first-log", cut.Markup);
    }

    [Fact]
    public void FirstBlock_AsksPageOneOfFifty()
    {
        var cut = RenderSection();
        cut.WaitForState(() => cut.Markup.Contains("Build started"));
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers/1/logs", StringComparison.Ordinal)
            && r.Url.Contains("page=1", StringComparison.Ordinal)
            && r.Url.Contains("pageSize=50", StringComparison.Ordinal));
    }

    [Fact]
    public void Loading_InitiallyFalse()
    {
        var cut = RenderSection();
        cut.WaitForState(() => cut.Markup.Contains("Build started"));
        var loading = (bool)typeof(ServerLogsSection).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    /// <summary>The grid reads older entries block by block: the second block of 50 is page 2, and it
    /// replaces the rows the section hands the grid for that block (replaces the former Load more button).</summary>
    [Fact]
    public async Task LoadLogsAsync_SecondBlock_AsksPageTwo()
    {
        var cut = RenderSection();
        cut.WaitForState(() => cut.Markup.Contains("Build started"));
        _handler.SetJsonResponse(HttpMethod.Get, "page=2", new PaginatedResult<TaskLogDto>
        {
            Items = [new TaskLogDto { Id = 3, Message = "Deploy started", Level = TaskLogLevel.Info }],
            TotalCount = 51
        });

        await cut.InvokeAsync(() => InvokeLoad(cut, new GridLoadArgs { Skip = 50, Top = 50 }));

        Assert.Contains(_handler.Requests, r => r.Url.Contains("page=2", StringComparison.Ordinal));
        var logs = (List<TaskLogDto>)typeof(ServerLogsSection).GetField("_logs", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("Deploy started", Assert.Single(logs).Message);
        Assert.Equal(51, (int)typeof(ServerLogsSection).GetField("_totalCount", Priv)!.GetValue(cut.Instance)!);
        Assert.False((bool)typeof(ServerLogsSection).GetField("_loading", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task LoadLogsAsync_Failure_ShowsTheErrorAndARetryClearsIt()
    {
        var cut = RenderSection();
        cut.WaitForState(() => cut.Markup.Contains("Build started"));
        _handler.SetResponse(HttpMethod.Get, "page=2", System.Net.HttpStatusCode.InternalServerError);

        await cut.InvokeAsync(() => InvokeLoad(cut, new GridLoadArgs { Skip = 50, Top = 50 }));

        Assert.True((bool)typeof(ServerLogsSection).GetField("_loadFailed", Priv)!.GetValue(cut.Instance)!);
        cut.Render();
        Assert.Contains("LoadFailed", cut.Markup);

        _handler.SetJsonResponse(HttpMethod.Get, "page=2", MakeLogPage(
            new TaskLogDto { Id = 3, Message = "retry", Level = TaskLogLevel.Info }));
        await cut.InvokeAsync(() => InvokeLoad(cut, new GridLoadArgs { Skip = 50, Top = 50 }));

        Assert.False((bool)typeof(ServerLogsSection).GetField("_loadFailed", Priv)!.GetValue(cut.Instance)!);
        Assert.Equal(2, _handler.Requests.Count(request => request.Url.Contains("page=2", StringComparison.Ordinal)));
    }

    /// <summary>The level multi-select and the time range set in the headers reach the API, so they
    /// narrow the whole log of the server and not only the loaded entries.</summary>
    [Fact]
    public async Task HeaderFilters_LevelAndTimeRange_ReachTheApi()
    {
        var cut = RenderSection();
        cut.WaitForState(() => cut.Markup.Contains("Build started"));
        var grid = cut.FindComponent<AetheusDataGrid<TaskLogDto>>().Instance.Grid!;
        var levels = OmniDataGridFilterValues.Join(["Error", "Warning"]);

        await cut.InvokeAsync(() => grid.SetFiltersAsync(new Dictionary<string, string?>
        {
            ["Level"] = levels,
            ["Timestamp"] = OmniDataGridDateRange.Join("2026-09-01", "2026-09-02")
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/servers/1/logs", StringComparison.Ordinal)
                && url.Contains("Field=Level", StringComparison.Ordinal)
                && url.Contains("Operator=In", StringComparison.Ordinal)
                && url.Contains(levels, StringComparison.Ordinal)
                && url.Contains("Field=Timestamp", StringComparison.Ordinal)
                && url.Contains("Operator=GreaterThanOrEqual", StringComparison.Ordinal);
        }));
    }

    [Fact]
    public async Task LoadLogsAsync_Sort_ReachesTheApi()
    {
        var cut = RenderSection();
        cut.WaitForState(() => cut.Markup.Contains("Build started"));

        await cut.InvokeAsync(() => InvokeLoad(cut, new GridLoadArgs
        {
            Skip = 0,
            Top = 50,
            Sorts = [new GridSortDescriptor("Level", GridSortOrder.Descending)]
        }));

        Assert.Contains(_handler.Requests, r => r.Url.Contains("sortBy=Level", StringComparison.Ordinal)
            && r.Url.Contains("sortDescending=True", StringComparison.Ordinal));
    }

    private static Task InvokeLoad(IRenderedComponent<ServerLogsSection> cut, GridLoadArgs args) =>
        (Task)typeof(ServerLogsSection).GetMethod("LoadLogsAsync", Priv)!.Invoke(cut.Instance, [args])!;

    [Theory]
    [InlineData(TaskLogLevel.Error, OmniTone.Danger)]
    [InlineData(TaskLogLevel.Warning, OmniTone.Warning)]
    [InlineData(TaskLogLevel.Info, OmniTone.Accent)]
    public void GetLevelBadge_ReturnsExpected(TaskLogLevel level, OmniTone expected)
    {
        var method = typeof(ServerLogsSection).GetMethod("GetLevelBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object)level])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetLevelBadge_UnknownLevel_ReturnsLight()
    {
        var method = typeof(ServerLogsSection).GetMethod("GetLevelBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object)(TaskLogLevel)99])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    [Fact]
    public async Task TheExport_ReadsTheRequestedPageOfThisServersLogs_InTheGridsSort()
    {
        var cut = RenderSection(serverId: 3);
        cut.WaitForState(() => cut.Markup.Contains("Build started"));
        var grid = cut.FindComponent<AetheusDataGrid<TaskLogDto>>().Instance;

        var page = await cut.InvokeAsync(() => grid.ExportRead!(new GridLoadArgs
        {
            Skip = 100,
            Top = 50,
            Sorts = [new GridSortDescriptor("Timestamp", GridSortOrder.Descending)],
        }));

        Assert.Equal(["Build started", "Build failed"], page.Items.Select(log => log.Message));
        var url = _handler.Requests.Last(r => r.Url.Contains("api/servers/3/logs", StringComparison.Ordinal)).Url;
        Assert.Contains("page=3", url, StringComparison.Ordinal);
        Assert.Contains("pageSize=50", url, StringComparison.Ordinal);
        Assert.Contains("sortBy=Timestamp", url, StringComparison.Ordinal);
        Assert.Contains("sortDescending=True", url, StringComparison.Ordinal);
    }
}
