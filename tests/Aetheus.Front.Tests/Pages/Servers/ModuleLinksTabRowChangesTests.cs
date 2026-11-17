// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-10 on the module links grid: a created link shows at once (derived from the answer as the server lists it)
/// and an unlinked one leaves the table, before the list is read again and without the grid's blocking bar; a refused
/// create or unlink (a 409 included) leaves the rows as they were and says so. The automatic detection answers no link,
/// so its read is quiet: the grid shows no bar.</summary>
public sealed class ModuleLinksTabRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string PageUrl = "api/servers/1/module-links/resource-page";
    private const string LinksUrl = "api/servers/1/module-links";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public ModuleLinksTabRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse(HttpMethod.Post, PageUrl, Page(Link(1, ModuleLinkType.Apache, "default-site"), Link(2, ModuleLinkType.Certbot, "example.org")));
    }

    private static LinkedResourceDto Link(int id, ModuleLinkType type, string identifier) =>
        new() { LinkId = id, Type = type, Identifier = identifier };

    private static PaginatedResult<LinkedResourceDto> Page(params LinkedResourceDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<LinkedResourceDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<LinkedResourceDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, PageUrl, _ => gate.Task);
        return gate;
    }

    private int PageReads() => _handler.RequestsSnapshot().Count(r => r.Method == "POST" && r.Url.Contains(PageUrl, StringComparison.Ordinal));

    private IRenderedComponent<ModuleLinksTab> RenderTab()
    {
        var cut = Render<ModuleLinksTab>(p => p
            .Add(x => x.ServerId, 1)
            .Add(x => x.SourceType, ModuleLinkType.Docker)
            .Add(x => x.Resources, ["nginx"]));
        cut.WaitForAssertion(() => Assert.Contains("example.org", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public async Task ACreatedLink_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderTab();
        var grid = cut.FindComponent<AetheusDataGrid<LinkedResourceDto>>().Instance;
        _dialogs.OpenResult = new AddModuleLinkResult(ModuleLinkType.Apache, "blog-site", "nginx");
        _handler.SetJsonResponse(HttpMethod.Post, LinksUrl, new ModuleLinkDto
        {
            Id = 3,
            SourceType = ModuleLinkType.Docker,
            SourceIdentifier = "nginx",
            TargetType = ModuleLinkType.Apache,
            TargetIdentifier = "blog-site"
        });
        var gate = HoldTheNextListRead();

        await cut.Find("button[title='AddLink']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("blog-site", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("example.org", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Link(1, ModuleLinkType.Apache, "default-site"), Link(2, ModuleLinkType.Certbot, "example.org"), Link(3, ModuleLinkType.Apache, "blog-site")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("blog-site", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedCreate_LeavesTheRowsAsTheyWere_AndReportsTheError()
    {
        var cut = RenderTab();
        var grid = cut.FindComponent<AetheusDataGrid<LinkedResourceDto>>().Instance;
        _dialogs.OpenResult = new AddModuleLinkResult(ModuleLinkType.Apache, "blog-site", "nginx");
        _handler.SetResponse(HttpMethod.Post, LinksUrl, HttpStatusCode.Conflict);

        await cut.Find("button[title='AddLink']").ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);

        Assert.DoesNotContain("blog-site", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("example.org", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Summary == "ActionFailed");
    }

    [Fact]
    public async Task AnUnlinkedResource_LeavesTheGridAtOnce_AndARefusedUnlinkKeepsIt()
    {
        var cut = RenderTab();
        var grid = cut.FindComponent<AetheusDataGrid<LinkedResourceDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/servers/1/module-links/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Unlink']")[1].ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("example.org", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Summary == "ActionFailed");

        _handler.SetResponse(HttpMethod.Delete, "api/servers/1/module-links/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        await cut.FindAll("button[title='Unlink']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("example.org", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("default-site", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Link(1, ModuleLinkType.Apache, "default-site")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("example.org", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAutomaticDetection_ReadsTheListAgainBehindTheRows_WithoutTheBar()
    {
        var cut = RenderTab();
        var grid = cut.FindComponent<AetheusDataGrid<LinkedResourceDto>>().Instance;
        _handler.SetResponse(HttpMethod.Post, "api/servers/1/module-links/auto-detect", HttpStatusCode.OK);
        var gate = HoldTheNextListRead();

        var reads = PageReads();
        var detect = cut.FindMenuItem("Detect")!;
        var click = detect.ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(reads + 1, PageReads()));
        Assert.False(click.IsCompleted);
        Assert.Contains("example.org", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Link(1, ModuleLinkType.Apache, "default-site"), Link(2, ModuleLinkType.Certbot, "example.org"), Link(4, ModuleLinkType.Apache, "found-site")));
        await click;
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("found-site", cut.Markup, StringComparison.Ordinal);
    }
}
