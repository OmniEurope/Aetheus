// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-10 on the server applications grid: a created application shows what the create answered and a deleted one
/// leaves the table, before the list is read again and without the grid's blocking bar; a refused create or delete
/// (a 409 included) leaves the rows as they were and says so.</summary>
public sealed class ServerAppsSectionRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string ListUrl = "api/servers/7/apps?";
    private const string AppsUrl = "api/servers/7/apps";
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerAppsSectionRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        ((ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        _handler.SetJsonResponse("api/servers/7/apps/filter-values", new ServerAppFilterValuesDto());
        _handler.SetJsonResponse(HttpMethod.Get, ListUrl, Page(App(1, "nginx"), App(2, "redis")));
    }

    private static ServerAppDto App(int id, string name) => new() { Id = id, Name = name, Source = "manual" };

    private static PaginatedResult<ServerAppDto> Page(params ServerAppDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<ServerAppDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<ServerAppDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, ListUrl, _ => gate.Task);
        return gate;
    }

    private IRenderedComponent<PageHeaderOutletHost>? _host;

    // The section is rendered next to the page header's actions outlet, where its Add button is sent.
    private IRenderedComponent<ServerAppsSection> RenderSection()
    {
        _host = this.RenderWithPageHeader<ServerAppsSection>(p => p.Add(x => x.ServerId, 7));
        var cut = _host.FindComponent<ServerAppsSection>();
        cut.WaitForAssertion(() => Assert.Contains("redis", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    // The real add flow: the header's Add opens the form, the name is typed, and the form's own Add button is clicked.
    private async Task Add(IRenderedComponent<ServerAppsSection> cut, string name)
    {
        await _host!.Find(".test-outlet").QuerySelectorAll("button")
            .First(button => button.TextContent.Contains("Add", StringComparison.Ordinal)).ClickAsync(new());
        cut.Find("input#oe-pages-servers-serverdetailsections-serverappssection-1").Input(name);
        await cut.FindAll("button").First(button => button.TextContent.Contains("Add", StringComparison.Ordinal)).ClickAsync(new());
    }

    [Fact]
    public async Task ACreatedApplication_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ServerAppDto>>().Instance;
        _handler.SetJsonResponse(HttpMethod.Post, AppsUrl, App(3, "postgres"));
        var gate = HoldTheNextListRead();

        await Add(cut, "postgres");

        cut.WaitForAssertion(() => Assert.Contains("postgres", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("nginx", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(App(1, "nginx"), App(2, "redis"), App(3, "postgres")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("postgres", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedCreate_LeavesTheRowsAsTheyWere_AndReportsTheError()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ServerAppDto>>().Instance;
        _handler.SetResponse(HttpMethod.Post, AppsUrl, HttpStatusCode.Conflict);

        await Add(cut, "nginx");
        await cut.InvokeAsync(() => grid.BackgroundRefresh);

        Assert.Contains("nginx", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("redis", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "SaveFailed");
    }

    [Fact]
    public async Task ADeletedApplication_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ServerAppDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/servers/7/apps/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("redis", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "DeleteFailed");

        _handler.SetResponse(HttpMethod.Delete, "api/servers/7/apps/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("redis", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("nginx", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(App(1, "nginx")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("redis", cut.Markup, StringComparison.Ordinal);
    }
}
