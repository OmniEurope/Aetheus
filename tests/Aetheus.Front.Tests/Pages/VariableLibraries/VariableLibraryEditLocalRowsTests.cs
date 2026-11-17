// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.VariableLibraries;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.VariableLibraries;

/// <summary>R-12: the "Allocate a port" of the add form creates the typed <c>PORT_</c> key with its port.
/// R-16: a row created, saved, allocated or deleted shows at once, before the read that follows ends.</summary>
public sealed class VariableLibraryEditLocalRowsTests : BunitContext
{
    private const string EntriesUrl = "api/variable-libraries/1/entries";
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string AllocateUrl = "api/variable-libraries/1/ports/allocate";
    private const string PreviewUrl = "api/variable-libraries/1/ports/free-preview";
    private static readonly VariableEntryDto Existing = new() { Id = 5, Key = "SITE_DOMAIN", Value = "aetheus.example", VersionCount = 1 };
    private readonly BunitTestHelper.TestHandler _handler;

    public VariableLibraryEditLocalRowsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/variable-libraries/1", new VariableLibraryDetailDto { Id = 1, Name = "lib", ProjectId = 5, EntryCount = 1 });
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", new PortAllocationTargetsDto
        {
            Servers = [new PortAllocationTargetDto { ServerId = 7, ServerName = "vps" }],
            PreselectedServerId = 7,
            ProjectId = 5
        });
    }

    private static PaginatedResult<VariableEntryDto> Page(params VariableEntryDto[] items) =>
        new() { Items = [.. items], TotalCount = items.Length, Page = 1, PageSize = 25 };

    private IRenderedComponent<VariableLibraryEdit> RenderWithExisting()
    {
        _handler.SetJsonResponse(HttpMethod.Get, EntriesUrl, Page(Existing));
        var cut = Render<VariableLibraryEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("SITE_DOMAIN", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    /// <summary>The entries read now waits for the returned release, so a test sees the page before it ends.</summary>
    private TaskCompletionSource<PaginatedResult<VariableEntryDto>> HoldTheNextEntriesRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<VariableEntryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, EntriesUrl, _ => gate.Task);
        return gate;
    }

    private static AngleSharp.Dom.IElement? PortItem(IRenderedComponent<VariableLibraryEdit> cut) => cut.FindMenuItem("PortFree");

    private bool AllocatePosted() => _handler.RequestsSnapshot().Any(request => request.Method == "POST"
        && request.Url.EndsWith(AllocateUrl, StringComparison.Ordinal));

    // ---- R-12 ----

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    [Fact]
    public async Task WithANameWithoutThePortPrefix_TheItemLooksUnavailableAndItsClickWarnsWithoutAllocating()
    {
        var cut = RenderWithExisting();
        cut.Find("input[aria-label='Key']").Input("DB_HOST");

        var item = PortItem(cut)!;
        Assert.Contains("menu-item-unavailable", item.ClassName, StringComparison.Ordinal);
        Assert.False(item.HasAttribute("disabled"));
        Assert.DoesNotContain("PortKeyMustStartWithPrefix", cut.Markup, StringComparison.Ordinal);

        await item.ClickAsync();

        Assert.Contains("PortKeyMustStartWithPrefix", cut.Markup, StringComparison.Ordinal);
        Assert.False(AllocatePosted());
        Assert.Equal(0, Dialog.OpenCount);
    }

    [Fact]
    public async Task WithAnEmptyKey_TheClickAsksForAKeyWithoutWarningNorRequest()
    {
        var cut = RenderWithExisting();

        var item = PortItem(cut)!;
        Assert.DoesNotContain("menu-item-unavailable", item.ClassName, StringComparison.Ordinal);
        await item.ClickAsync();

        Assert.Contains("PortTypeKeyFirst", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PortKeyMustStartWithPrefix", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, Dialog.OpenCount);
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method == "POST" && request.Url.EndsWith(PreviewUrl, StringComparison.Ordinal));
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "Aetheus.focusById" && call.Arguments.Contains("library-new-entry-key"));
    }

    [Fact]
    public async Task WhenTheLibraryCannotAllocate_SaysToGoThroughAllocatePortsAndAsksNothing()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "/ports/servers", new PortAllocationTargetsDto { Servers = [], Blocker = PortAllocationBlocker.NoProject });
        var cut = RenderWithExisting();
        cut.Find("input[aria-label='Key']").Input("PORT_NEW");

        await PortItem(cut)!.ClickAsync();

        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger
            && toast.Detail.Contains("PortFreeNeedsOneServer", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method == "POST" && request.Url.EndsWith(PreviewUrl, StringComparison.Ordinal));
        Assert.Equal(string.Empty, cut.Find("input[aria-label='Value']").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public async Task WithThePortPrefix_FillsTheValueFieldWithAFreePortAndCreatesNothing()
    {
        _handler.SetJsonResponse(HttpMethod.Post, PreviewUrl, new List<int> { 10031 });
        var cut = RenderWithExisting();
        cut.Find("input[aria-label='Key']").Input("PORT_NEW");
        Assert.DoesNotContain("menu-item-unavailable", PortItem(cut)!.ClassName, StringComparison.Ordinal);

        await PortItem(cut)!.ClickAsync();

        var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST"
            && request.Url.EndsWith(PreviewUrl, StringComparison.Ordinal));
        Assert.Contains("\"serverId\":7", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Url.Contains("reserve", StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Equal("10031", cut.Find("input[aria-label='Value']").GetAttribute("value")));
        Assert.Equal("PORT_NEW", cut.Find("input[aria-label='Key']").GetAttribute("value"));
        Assert.False(AllocatePosted());
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Method == "POST" && request.Url.EndsWith(EntriesUrl, StringComparison.Ordinal));
        Assert.DoesNotContain("PortKeyMustStartWithPrefix", cut.Markup, StringComparison.Ordinal);
    }

    // ---- R-16 ----

    [Fact]
    public async Task AnAddedRow_ShowsBeforeTheServerReadEnds_AndTheReadCorrectsIt()
    {
        _handler.SetJsonResponse(HttpMethod.Post, EntriesUrl, new VariableEntryDto { Id = 6, Key = "NEW_KEY", Value = "v", VersionCount = 1 });
        var cut = RenderWithExisting();
        var gate = HoldTheNextEntriesRead();

        cut.Find("input[aria-label='Key']").Input("NEW_KEY");
        await cut.Find("button[type='submit']").ClickAsync();

        // The read of the entries was started and is held: the row on screen came from the create answer.
        await _handler.WhenRequestedAsync(
            request => request.Method == "GET" && request.Url.Contains(EntriesUrl, StringComparison.Ordinal), 2, "the read behind the add");
        Assert.False(cut.Instance.BackgroundRefresh.IsCompleted);
        cut.WaitForAssertion(() => Assert.Contains("NEW_KEY", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("SITE_DOMAIN", cut.Markup, StringComparison.Ordinal);

        // The server is the source of truth: its answer wins (here it holds the value "server").
        gate.SetResult(Page(Existing, new VariableEntryDto { Id = 6, Key = "NEW_KEY", Value = "server", VersionCount = 1 }));
        await cut.InvokeAsync(() => cut.Instance.BackgroundRefresh);
        cut.WaitForAssertion(() => Assert.Contains("server", cut.Markup, StringComparison.Ordinal));
    }

    private static AetheusDataGrid<VariableEntryDto> GridOf(IRenderedComponent<VariableLibraryEdit> cut) =>
        cut.FindComponent<AetheusDataGrid<VariableEntryDto>>().Instance;

    [Fact]
    public async Task TheReadBehindAnAdd_DrawsNoLoadingBar_AndTheRowsStayOnScreen()
    {
        _handler.SetJsonResponse(HttpMethod.Post, EntriesUrl, new VariableEntryDto { Id = 6, Key = "NEW_KEY", Value = "v", VersionCount = 1 });
        var cut = RenderWithExisting();
        var gate = HoldTheNextEntriesRead();

        cut.Find("input[aria-label='Key']").Input("NEW_KEY");
        await cut.Find("button[type='submit']").ClickAsync();

        await _handler.WhenRequestedAsync(
            request => request.Method == "GET" && request.Url.Contains(EntriesUrl, StringComparison.Ordinal), 2, "the read behind the add");
        cut.WaitForAssertion(() => Assert.Contains("NEW_KEY", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));
        Assert.Contains("SITE_DOMAIN", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Page(Existing, new VariableEntryDto { Id = 6, Key = "NEW_KEY", Value = "v", VersionCount = 1 }));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.Contains("NEW_KEY", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedRow_LeavesTheGridBeforeTheServerReadEnds()
    {
        _handler.SetResponse(HttpMethod.Delete, EntriesUrl + "/5", System.Net.HttpStatusCode.OK);
        var cut = RenderWithExisting();
        var gate = HoldTheNextEntriesRead();
        // The row's own menu: the page header has a "Delete" of its own, for the whole library.
        cut.Find("tbody .omni-overflow-menu__trigger").Click();
        var item = cut.FindAll(".omni-menu__item").First(entry => entry.Names().Contains("Delete", StringComparison.Ordinal));

        ((ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        await item.ClickAsync();

        cut.WaitForAssertion(() => Assert.DoesNotContain("SITE_DOMAIN", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(Bar));
        gate.SetResult(Page());
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.DoesNotContain("SITE_DOMAIN", cut.Markup, StringComparison.Ordinal);
    }
}
