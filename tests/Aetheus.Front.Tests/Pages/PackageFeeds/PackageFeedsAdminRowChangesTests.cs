// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.PackageFeeds;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.PackageFeeds;

/// <summary>R-10 on the package feeds grid: a deleted feed leaves the table before the list is read again and without the
/// grid's blocking bar, a refused deletion (a 409 included) keeps the row and says so. A feed created in the dialog is read
/// again behind the rows, quietly: the dialog answers no row, and the grid shows no bar while it waits.</summary>
public sealed class PackageFeedsAdminRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string ListUrl = "api/package-feeds?";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public PackageFeedsAdminRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialogs.ConfirmResult = true;
        _handler.SetJsonResponse(HttpMethod.Get, ListUrl, Page(Feed(1, "corp-nuget"), Feed(2, "corp-npm")));
    }

    private static PackageFeedDto Feed(int id, string name) => new() { Id = id, Name = name, FeedType = PackageFeedType.NuGet, UpstreamUrl = "https://api.nuget.org" };

    private static PaginatedResult<PackageFeedDto> Page(params PackageFeedDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private TaskCompletionSource<PaginatedResult<PackageFeedDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<PackageFeedDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, ListUrl, _ => gate.Task);
        return gate;
    }

    private IRenderedComponent<PackageFeedsAdmin> RenderPage()
    {
        var cut = Render<PackageFeedsAdmin>();
        cut.WaitForAssertion(() => Assert.Contains("corp-npm", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public async Task ADeletedFeed_LeavesTheGridAtOnce_AndARefusedDeletionKeepsIt()
    {
        var cut = RenderPage();
        var grid = cut.FindComponent<AetheusDataGrid<PackageFeedDto>>().Instance;

        _handler.SetResponse(HttpMethod.Delete, "api/package-feeds/2", HttpStatusCode.Conflict);
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("corp-npm", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "DeleteFailed");

        _handler.SetResponse(HttpMethod.Delete, "api/package-feeds/2", HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("corp-npm", cut.Markup, StringComparison.Ordinal));
        Assert.False(grid.BackgroundRefresh.IsCompleted);
        Assert.Contains("corp-nuget", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Feed(1, "corp-nuget")));
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.DoesNotContain("corp-npm", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACreatedFeed_IsReadAgainBehindTheRows_WithoutTheBar()
    {
        var cut = RenderPage();
        var grid = cut.FindComponent<AetheusDataGrid<PackageFeedDto>>().Instance;
        _dialogs.OpenResult = true;
        var gate = HoldTheNextListRead();

        // The page header's "Create" button is in the page's own markup: the dialog flow is entered by clicking it.
        var create = cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(2, _handler.RequestsSnapshot().Count(r => r.Method == "GET" && r.Url.Contains(ListUrl, StringComparison.Ordinal))));
        Assert.False(create.IsCompleted);
        Assert.Contains("corp-npm", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Feed(1, "corp-nuget"), Feed(2, "corp-npm"), Feed(3, "corp-pypi")));
        await create;
        await cut.InvokeAsync(() => grid.BackgroundRefresh);
        Assert.Contains("corp-pypi", cut.Markup, StringComparison.Ordinal);
    }
}
