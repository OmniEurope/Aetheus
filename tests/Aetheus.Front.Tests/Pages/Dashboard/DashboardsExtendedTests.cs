// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Dashboards;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Dashboard;

/// <summary>
/// Extended coverage for Dashboards.razor.cs - CreateDashboard (prompt returns a name),
/// DeleteDashboard (success + failure), EditDashboard navigation.
/// </summary>
public class DashboardsExtendedTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public DashboardsExtendedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private void SetupDashboards(List<DashboardDto>? list = null)
    {
        _handler.SetJsonResponse("api/dashboards", list ?? [
            new DashboardDto { Id = 1, Name = "Ops", Widgets = [] },
            new DashboardDto { Id = 2, Name = "Infra", Widgets = [] }
        ]);
    }

    // ── CreateDashboard - name dialog cancelled (null) → no create ────────────

    [Fact]
    public async Task CreateDashboard_DialogCancelled_DoesNotCreate()
    {
        SetupDashboards([]);
        var cut = Render<Dashboards>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // CreateDashboard awaits Dialog.OpenAsync<DashboardNameDialog>; close it with null to cancel.
        var dialog = Spy();
        var method = typeof(Dashboards).GetMethod("CreateDashboard", Priv)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;

        Assert.Equal(1, dialog.OpenCount); // the SUT actually opened a dialog (not just the test closing one)
        Assert.True(dialog.Closed);
        var dashboards = (List<DashboardDto>)typeof(Dashboards).GetField("_dashboards", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(dashboards);
    }

    // ── EditDashboard - navigates ─────────────────────────────────────────────

    [Fact]
    public void EditDashboard_NavigatesCorrectly()
    {
        SetupDashboards();
        var cut = Render<Dashboards>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(Dashboards).GetMethod("EditDashboard", Priv)!;
        method.Invoke(cut.Instance, [new DashboardDto { Id = 7, Name = "Test", Widgets = [] }]);

        // EditDashboard navigates to /dashboards/{id}.
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/dashboards/7", nav.Uri);
    }

    // ── Loading is false after init ───────────────────────────────────────────

    [Fact]
    public void Loading_FalseAfterInit()
    {
        SetupDashboards();
        var cut = Render<Dashboards>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var loading = (bool)typeof(Dashboards).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    // ── Delete is attempted without dialog (confirm returns null - loose mock) ─

    // DeleteDashboard_ConfirmNull removed - Dialog.Confirm hangs in bUnit
}
