// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Plugins;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Plugins;

/// <summary>R-10 on the plugins grid: a registered, toggled or unregistered plugin changes its row from the action's
/// answer at once, and the read of the list behind it is quiet (no loading bar, no emptied table).</summary>
public sealed class PluginManagementRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public PluginManagementRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialog.ConfirmResult = true;
    }

    private static PluginRegistrationDto Plugin(int id, string name, PluginStatus status = PluginStatus.Enabled) =>
        new() { Id = id, Name = name, Version = "1.0", Type = PluginType.Collector, Status = status, EntryPoint = "entry.dll", CreatedAt = new DateTime(2026, 10, 1) };

    private static PaginatedResult<PluginRegistrationDto> Page(params PluginRegistrationDto[] items) =>
        new() { Items = [.. items], TotalCount = items.Length };

    private IRenderedComponent<PluginManagement> RenderPage(params PluginRegistrationDto[] plugins)
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/plugins/filter-values", new PluginFilterValuesDto());
        _handler.SetJsonResponse(HttpMethod.Get, "api/plugins?", Page(plugins));
        var cut = Render<PluginManagement>();
        cut.WaitForAssertion(() => Assert.Contains(plugins[0].Name, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private TaskCompletionSource<PaginatedResult<PluginRegistrationDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<PluginRegistrationDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/plugins?", _ => gate.Task);
        return gate;
    }

    private static AetheusDataGrid<PluginRegistrationDto> GridOf(IRenderedComponent<PluginManagement> cut) =>
        cut.FindComponent<AetheusDataGrid<PluginRegistrationDto>>().Instance;

    [Fact]
    public async Task ARegisteredPlugin_ShowsAtOnce_AndTheReadBehindItDrawsNoBar()
    {
        var cut = RenderPage(Plugin(1, "Alpha"));
        var gate = HoldTheNextListRead();
        var registered = Plugin(2, "Beta");
        _dialog.OpenResult = registered;

        await cut.FindAll("button").First(button => button.TextContent.Contains("Register", StringComparison.Ordinal)).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Beta", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Contains("Alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Plugin(1, "Alpha"), registered));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.Contains("Beta", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADismissedRegistration_ChangesNothing()
    {
        var cut = RenderPage(Plugin(1, "Alpha"));
        var readsBefore = _handler.RequestsSnapshot().Count(request => request.Url.Contains("api/plugins?", StringComparison.Ordinal));
        _dialog.OpenResult = false;

        await cut.FindAll("button").First(button => button.TextContent.Contains("Register", StringComparison.Ordinal)).ClickAsync(new());

        Assert.Equal(readsBefore, _handler.RequestsSnapshot().Count(request => request.Url.Contains("api/plugins?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AToggledPlugin_ShowsTheAnsweredStatusAtOnce_AndAFailedSaveKeepsTheRow()
    {
        var cut = RenderPage(Plugin(1, "Alpha"));
        _handler.SetResponse(HttpMethod.Put, "api/plugins/1", System.Net.HttpStatusCode.InternalServerError);

        await cut.Find("button[title='Edit']").ClickAsync(new());
        Assert.Contains("Alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Danger);

        _handler.SetJsonResponse(HttpMethod.Put, "api/plugins/1", Plugin(1, "Alpha-renamed-by-server", PluginStatus.Disabled));
        var gate = HoldTheNextListRead();
        await cut.Find("button[title='Edit']").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Alpha-renamed-by-server", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Plugin(1, "Alpha-renamed-by-server", PluginStatus.Disabled)));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.Contains("Alpha-renamed-by-server", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnregisteredPlugin_LeavesAtOnce_AndARefusedUnregistrationKeepsIt()
    {
        var cut = RenderPage(Plugin(1, "Alpha"), Plugin(2, "Beta"));
        _handler.SetResponse(HttpMethod.Delete, "api/plugins/2", System.Net.HttpStatusCode.Conflict);

        await cut.FindAll("button[title='Unregister']")[1].ClickAsync(new());
        Assert.Contains("Beta", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Danger);

        _handler.SetResponse(HttpMethod.Delete, "api/plugins/2", System.Net.HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        await cut.FindAll("button[title='Unregister']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("Beta", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Contains("Alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Plugin(1, "Alpha")));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.DoesNotContain("Beta", cut.Markup, StringComparison.Ordinal);
    }
}
