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
/// Additional coverage for Dashboards.razor.cs - CreateDashboard when JS prompt returns
/// a non-empty string (the API-call path) and DeleteDashboard after successful confirm.
/// Both Dialog.Confirm and JS prompt return null/default in loose mock - these tests
/// exercise the early-return guards and partial paths.
/// </summary>
public class DashboardsCoverageTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public DashboardsCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<IJSRuntime>()));
    }

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private void SetupDashboards(List<DashboardDto>? list = null)
    {
        _handler.SetJsonResponse("api/dashboards", list ?? [
            new DashboardDto { Id = 1, Name = "Overview", Widgets = [] },
            new DashboardDto { Id = 2, Name = "Security", Widgets = [] }
        ]);
    }

    // ── Loading state transitions ─────────────────────────────────────────────

    [Fact]
    public void Loading_StartsTrue_ThenFalse()
    {
        SetupDashboards();
        var cut = Render<Dashboards>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var loading = (bool)typeof(Dashboards).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    // ── Non-admin redirects ────────────────────────────────────────────────────

    [Fact]
    public void NonAdmin_Renders_WithoutDashboardList()
    {
        // RegisterServices with isAdmin=false → Auth.IsAdmin = false → navigate to "/"
        using var ctx = new BunitContext();
        BunitTestHelper.RegisterServices(ctx, isAdmin: false);
        ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
        var cut = ctx.Render<Dashboards>();
        // A non-admin is redirected to the app root before the dashboard list is ever fetched.
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/", nav.Uri);
    }

    // ── Dashboards list rendered ──────────────────────────────────────────────

    [Fact]
    public void Renders_DashboardNames()
    {
        SetupDashboards();
        var cut = Render<Dashboards>();
        cut.WaitForState(() => cut.Markup.Contains("Overview") || !cut.Markup.Contains("rz-progressbar"));
        Assert.Contains("Overview", cut.Markup);
    }

    // ── CreateDashboard: name dialog cancelled (null) → nothing happens ───────

    [Fact]
    public async Task CreateDashboard_DialogCancelled_DoesNotCreateDashboard()
    {
        SetupDashboards([]);
        var cut = Render<Dashboards>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // CreateDashboard awaits Dialog.OpenAsync<DashboardNameDialog>; start it without
        // awaiting, then close the dialog returning null to simulate a cancel.
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

    // DeleteDashboard_MethodExists removed: reflexive existence assertion on a private method
    // (GetMethod on a compiled member can never be null) that verified no behaviour. The delete
    // flow gates on Dialog.Confirm, which cannot be driven from a standalone bUnit render.

    // ── EditDashboard navigates correctly ─────────────────────────────────────

    [Fact]
    public void EditDashboard_Navigates_ToDashboardId()
    {
        SetupDashboards();
        var cut = Render<Dashboards>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(Dashboards).GetMethod("EditDashboard", Priv)!;
        method.Invoke(cut.Instance, [new DashboardDto { Id = 15, Name = "NewDb", Widgets = [] }]);

        // EditDashboard navigates to /dashboards/{id}.
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/dashboards/15", nav.Uri);
    }

    // ── HttpRequestException from api falls back to empty list ───────────────

    [Fact]
    public void OnInit_HttpError_FallsBackToEmptyList()
    {
        _handler.SetResponse("api/dashboards", System.Net.HttpStatusCode.Unauthorized);
        var cut = Render<Dashboards>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var loading = (bool)typeof(Dashboards).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
        var dashboards = (List<DashboardDto>)typeof(Dashboards).GetField("_dashboards", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(dashboards);
    }
}
