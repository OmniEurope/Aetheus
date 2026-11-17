// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Dashboards;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// R-36, loading level 2: while the menu is there and the content is not, ONE veil covers the content area only,
/// drawn by <c>MainLayout</c> (OE's OmniPageVeil) with the site's plane, and no other logo shows beside it. Level 3 is
/// <see cref="ZoneVeil"/>. The layout hosts the page as the router does, so the veil is read where the user sees it.
/// </summary>
public sealed class ContentAreaVeilTests : BunitContext
{
    private const string BodyWrapper = "[data-testid='app-body-wrapper']";
    private readonly BunitTestHelper.TestHandler _handler;

    public ContentAreaVeilTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        Services.AddSingleton<OmniDialogService>(sp => new Tests.TestDoubles.SpyDialogService(
            sp.GetRequiredService<NavigationManager>(), sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
    }

    private IRenderedComponent<MainLayout> RenderInLayout<TPage>() where TPage : IComponent =>
        Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(builder =>
        {
            builder.OpenComponent<TPage>(0);
            builder.CloseComponent();
        })));

    [Fact]
    public void AtRest_NoVeilIsUp_AndTheBodyIsReachable()
    {
        var layout = Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "page"))));

        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(BodyWrapper)));
        Assert.Empty(layout.FindAll(".omni-page-veil"));
        Assert.Equal("false", layout.Find(BodyWrapper).GetAttribute("aria-busy"));
        Assert.False(layout.Find(BodyWrapper).HasAttribute("inert"));
    }

    [Fact]
    public void AHeldVeil_CoversTheContentArea_WithTheSitesPlaneAlone_AndLiftsWhenReleased()
    {
        var layout = Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "page"))));
        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(BodyWrapper)));

        var hold = Services.GetRequiredService<BusyVeil>().Hold();

        layout.WaitForAssertion(() => Assert.Single(layout.FindAll(".omni-page-veil[role=status]")));
        var veil = layout.Find(".omni-page-veil");
        // The class that keeps the plane at the middle of the window, where the splash has it (app.css).
        Assert.Contains("site-content-veil", veil.ClassList);
        // The same plane as the splash and the component veil, and no second logo beside it.
        Assert.Single(layout.FindAll(".omni-page-veil__logo > svg.aetheus-loader-logo"));
        Assert.Single(layout.FindAll("svg.aetheus-loader-logo"));
        Assert.Empty(layout.FindAll(".omni-logo-loader"));
        Assert.Equal("true", layout.Find(BodyWrapper).GetAttribute("aria-busy"));
        Assert.True(layout.Find(BodyWrapper).HasAttribute("inert"));

        hold.Dispose();

        layout.WaitForAssertion(() => Assert.Empty(layout.FindAll(".omni-page-veil")));
        Assert.False(layout.Find(BodyWrapper).HasAttribute("inert"));
    }

    [Fact]
    public void APageReadingItsList_HoldsTheContentAreaVeil_NotALoaderOfItsOwn_ThenReleasesIt()
    {
        var answer = new TaskCompletionSource<List<DashboardDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/dashboards", _ => answer.Task);

        var layout = RenderInLayout<Dashboards>();

        layout.WaitForAssertion(() => Assert.Single(layout.FindAll(".omni-page-veil.site-content-veil")));
        Assert.Single(layout.FindAll("svg.aetheus-loader-logo"));
        Assert.Empty(layout.FindAll(".aetheus-loader"));

        answer.SetResult([]);

        layout.WaitForAssertion(() => Assert.Empty(layout.FindAll(".omni-page-veil")));
        Assert.Empty(layout.FindAll("svg.aetheus-loader-logo"));
    }

    [Fact]
    public void AZoneVeil_VeilsOnlyItsComponent_NotTheContentArea()
    {
        var layout = Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b =>
        {
            b.OpenComponent<ZoneVeil>(0);
            b.CloseComponent();
        })));

        layout.WaitForAssertion(() => Assert.Single(layout.FindAll(".zone-veil-host > .omni-page-veil--parent")));
        // Level 3 is not level 2: the body stays reachable and the layout's own veil is down.
        Assert.Empty(layout.FindAll(".site-content-veil"));
        Assert.False(layout.Find(BodyWrapper).HasAttribute("inert"));
        Assert.Single(layout.FindAll("svg.aetheus-loader-logo"));
    }

    [Fact]
    public void ZoneVeil_ShowsTheSitesPlaneWhileBusy_AndNothingOtherwise_AndAnnouncesItsLabel()
    {
        var cut = Render<ZoneVeil>(p => p.Add(x => x.Label, "LogsLoading"));

        Assert.Single(cut.FindAll(".zone-veil-host > .omni-page-veil--parent[role=status]"));
        Assert.Single(cut.FindAll(".omni-page-veil__logo > svg.aetheus-loader-logo"));
        Assert.Equal("LogsLoading", cut.Find(".omni-page-veil .omni-visually-hidden").TextContent);

        cut.Render(p => p.Add(x => x.Busy, false));

        Assert.Empty(cut.FindAll(".omni-page-veil"));
        Assert.NotEmpty(cut.FindAll(".zone-veil-host"));
    }
}
