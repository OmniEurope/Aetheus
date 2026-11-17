// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>R-10 on the project servers grid: an attached, saved or detached server shows what the action answered, before
/// the list is read again and without the grid's blocking bar; a refused action (a 409 included) leaves the rows as they
/// were and says so.</summary>
public sealed class ProjectServersSectionRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string ListUrl = "api/projects/10/servers?";
    private const string ServersUrl = "api/projects/10/servers";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public ProjectServersSectionRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialogs.ConfirmResult = true;
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto> { Items = [], TotalCount = 0, Page = 1, PageSize = 200 });
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects/10", new ProjectDetailDto { Id = 10, Name = "site" });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetJsonResponse(HttpMethod.Get, ListUrl, Page(Server(1, "web-front"), Server(2, "db-back")));
    }

    private static ProjectServerDto Server(int id, string name) =>
        new() { Id = id, ProjectId = 10, Type = ProjectServerType.ExternalHost, DisplayName = name, Host = "10.0.0.1", Port = 22 };

    private static PaginatedResult<ProjectServerDto> Page(params ProjectServerDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<ProjectServerDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<ProjectServerDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, ListUrl, _ => gate.Task);
        return gate;
    }

    private IRenderedComponent<PageHeaderOutletHost>? _host;

    // The section is rendered next to the page header's actions outlet, where its Add button is sent.
    private IRenderedComponent<ProjectServersSection> RenderSection()
    {
        _host = this.RenderWithPageHeader<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));
        var cut = _host.FindComponent<ProjectServersSection>();
        cut.WaitForAssertion(() => Assert.Contains("db-back", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static ProjectServerDialogResult Typed(string name) =>
        new() { Type = ProjectServerType.ExternalHost, DisplayName = name, Host = "10.0.0.9", Port = 22 };

    // The page-header "Add" sits in the section outlet the host renders: the dialog flow is entered by clicking it.
    private Task ClickAddAsync(IRenderedComponent<ProjectServersSection> cut) =>
        _host!.Find(".test-outlet").QuerySelectorAll("button")
            .First(button => button.TextContent.Contains("Add", StringComparison.Ordinal)).ClickAsync(new());

    [Fact]
    public async Task AnAttachedServer_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ProjectServerDto>>().Instance;
        _dialogs.OpenResult = Typed("cache-node");
        _handler.SetJsonResponse(HttpMethod.Post, ServersUrl, Server(3, "cache-node"));
        var gate = HoldTheNextListRead();

        await ClickAddAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("cache-node", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("web-front", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Server(1, "web-front"), Server(2, "db-back"), Server(3, "cache-node")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("cache-node", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedAttach_LeavesTheRowsAsTheyWere_AndReportsTheError()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ProjectServerDto>>().Instance;
        _dialogs.OpenResult = Typed("cache-node");
        _handler.SetResponse(HttpMethod.Post, ServersUrl, HttpStatusCode.Conflict);

        await ClickAddAsync(cut);
        await cut.InvokeAsync(() => grid.BackgroundRefresh);

        Assert.DoesNotContain("cache-node", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("db-back", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "SaveFailed");
    }

    [Fact]
    public async Task ASavedServer_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ProjectServerDto>>().Instance;
        _dialogs.OpenResult = Typed("db-primary");
        _handler.SetJsonResponse(HttpMethod.Put, "api/projects/10/servers/2", Server(2, "db-primary"));
        var gate = HoldTheNextListRead();

        await cut.FindAll("button[title='Edit']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("db-primary", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("db-back", cut.Markup, StringComparison.Ordinal);
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Server(1, "web-front"), Server(2, "db-primary")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("db-primary", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedSave_KeepsTheRowAsItWas_AndReportsTheError()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ProjectServerDto>>().Instance;
        _dialogs.OpenResult = Typed("db-primary");
        _handler.SetResponse(HttpMethod.Put, "api/projects/10/servers/2", HttpStatusCode.Conflict);

        await cut.FindAll("button[title='Edit']")[1].ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);

        Assert.Contains("db-back", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("db-primary", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "SaveFailed");
    }

    [Fact]
    public async Task ADetachedServer_LeavesTheGridAtOnce_AndARefusedDetachKeepsIt()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ProjectServerDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/projects/10/servers/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("db-back", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "DeleteFailed");

        _handler.SetResponse(HttpMethod.Delete, "api/projects/10/servers/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("db-back", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("web-front", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Server(1, "web-front")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("db-back", cut.Markup, StringComparison.Ordinal);
    }

    // The overview checklist counts the project's servers: a detach reads the shared project detail again, as an attach and a
    // save do, and a refused detach (a 409 included) does not.
    [Fact]
    public async Task ADetachedServer_ReadsTheProjectDetailAgain_ARefusedDetachDoesNot()
    {
        var cut = RenderSection();
        var grid = cut.FindComponent<AetheusDataGrid<ProjectServerDto>>().Instance;
        int DetailReads() => _handler.RequestsSnapshot()
            .Count(request => request.Method == "GET" && request.Url.EndsWith("api/projects/10", StringComparison.Ordinal));
        var before = DetailReads();

        _handler.SetResponse(HttpMethod.Delete, "api/projects/10/servers/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "DeleteFailed");
        Assert.Equal(before, DetailReads());

        _handler.SetResponse(HttpMethod.Delete, "api/projects/10/servers/2", HttpStatusCode.NoContent);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);

        cut.WaitForAssertion(() => Assert.Equal(before + 1, DetailReads()));
    }
}
