// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages;

public class DashboardsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public DashboardsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    [Fact]
    public void Renders_Loading_Initially()
    {
        // FRONTTEST-004: the answer is held back, so the product loader is observed while the list is
        // pending, then replaced by the empty state once the empty list has arrived.
        var answer = new TaskCompletionSource<List<DashboardDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/dashboards", _ => answer.Task);
        var cut = Render<Dashboards>();
        Assert.Single(cut.FindComponents<AetheusLoader>());
        Assert.DoesNotContain("NoRecords", cut.Markup);

        answer.SetResult([]);

        // Loaded-machine ceiling: the page has no hub and renders once its list is read, so a failure left
        // here was only the answer's thread-pool hop not yet back (bUnit "Check count: 0" or 1).
        cut.WaitForAssertion(() => Assert.Empty(cut.FindComponents<AetheusLoader>()));
        Assert.Contains("NoRecords", cut.Markup);
    }

    [Fact]
    public void Renders_NoRecords_WhenEmpty()
    {
        _handler.SetJsonResponse("api/dashboards", new List<DashboardDto>());
        var cut = Render<Dashboards>();
        cut.WaitForState(() => cut.Markup.Contains("NoRecords"));
        Assert.Contains("NoRecords", cut.Markup);
    }

    [Fact]
    public void Renders_DashboardCards()
    {
        _handler.SetJsonResponse("api/dashboards", new List<DashboardDto>
        {
            new() { Id = 1, Name = "My Board", IsDefault = true, Widgets = [new DashboardWidgetDto { Id = 1, Title = "W1", WidgetType = DashboardWidgetType.ServerCount }], CreatedAt = DateTime.UtcNow },
            new() { Id = 2, Name = "Board 2", Widgets = [], CreatedAt = DateTime.UtcNow }
        });

        var cut = Render<Dashboards>();
        cut.WaitForState(() => cut.Markup.Contains("My Board"));

        Assert.Contains("My Board", cut.Markup);
        Assert.Contains("Board 2", cut.Markup);
        Assert.Contains("IsDefault", cut.Markup);
    }

    [Fact]
    public void RedirectsToHome_WhenNotAdmin()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false);
        handler.SetJsonResponse("api/dashboards", new List<DashboardDto>());
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = Render<Dashboards>();

        Assert.EndsWith("/", nav.Uri);
    }

    [Fact]
    public void CreateButton_IsRendered()
    {
        _handler.SetJsonResponse("api/dashboards", new List<DashboardDto>());
        var cut = Render<Dashboards>();
        cut.WaitForState(() => cut.Markup.Contains("Create"));

        Assert.Contains("Create", cut.Markup);
    }

    [Fact]
    public void EditButton_NavigatesToDashboardEdit()
    {
        _handler.SetJsonResponse("api/dashboards", new List<DashboardDto>
        {
            new() { Id = 7, Name = "Test", Widgets = [], CreatedAt = DateTime.UtcNow }
        });

        // FRONTTEST-007: the Edit button is clicked, from a page that is not the destination.
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/dashboards");
        var cut = Render<Dashboards>();
        cut.WaitForState(() => cut.Markup.Contains("Test"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Edit")).Click();

        Assert.Equal("/dashboards/7", new Uri(nav.Uri).AbsolutePath);
    }

    [Fact]
    public async Task CreateDashboard_CancelledDialog_CreatesNothing()
    {
        _handler.SetJsonResponse("api/dashboards", new List<DashboardDto>());

        var cut = Render<Dashboards>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar"));

        // CreateDashboard awaits Dialog.OpenAsync<DashboardNameDialog>; close it with null to cancel.
        var dialog = Spy();
        var method = typeof(Dashboards).GetMethod("CreateDashboard",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.Equal(1, dialog.OpenCount); // the SUT actually opened a dialog (not just the test closing one)
        Assert.True(dialog.Closed);
        // Cancelling the name dialog (null result) short-circuits before the create POST,
        // so the list stays empty and no POST is sent.
        var dashboards = (List<DashboardDto>)typeof(Dashboards).GetField("_dashboards",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Empty(dashboards);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST");
    }

    // FRONTTEST-007: the name dialog answers a name; the dashboard is created under that name and the
    // reloaded list shows it.
    [Fact]
    public async Task CreateDashboard_AddsToList()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/dashboards", new List<DashboardDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/dashboards", new DashboardDto { Id = 5, Name = "Ops board", Widgets = [] });
        var cut = Render<Dashboards>();
        cut.WaitForState(() => cut.Markup.Contains("NoRecords"));
        _handler.SetJsonResponse(HttpMethod.Get, "api/dashboards", new List<DashboardDto>
        {
            new() { Id = 5, Name = "Ops board", Widgets = [], CreatedAt = DateTime.UtcNow }
        });
        var dialog = Spy();

        // Clicked like a user does, so the page re-renders when its handler completes.
        var click = cut.FindAll("button").First(b => b.TextContent.Contains("Create")).ClickAsync(new());
        await cut.InvokeAsync(() => dialog.Close("  Ops board  "));
        await click;

        var body = _handler.RequestDetails.Single(r => r.Method == "POST" && r.Url.EndsWith("api/dashboards", StringComparison.Ordinal)).Body!;
        var request = System.Text.Json.JsonSerializer.Deserialize<CreateDashboardRequest>(body, JsonOptions.Web)!;
        Assert.Equal("Ops board", request.Name);
        cut.WaitForAssertion(() => Assert.Contains("Ops board", cut.Markup));
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success && toast.Detail == "DashboardCreated");
    }

    [Fact]
    public void EditDashboard_NavigatesToEdit()
    {
        _handler.SetJsonResponse("api/dashboards", new List<DashboardDto>
        {
            new() { Id = 3, Name = "NavTest", Widgets = [], CreatedAt = DateTime.UtcNow }
        });

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var cut = Render<Dashboards>();
        cut.WaitForState(() => cut.Markup.Contains("NavTest"));

        var method = typeof(Dashboards).GetMethod("EditDashboard",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [new DashboardDto { Id = 3, Name = "NavTest" }]);

        Assert.Contains("dashboards/3", nav.Uri);
    }
}
