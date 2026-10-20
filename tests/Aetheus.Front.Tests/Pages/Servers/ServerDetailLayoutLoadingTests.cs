// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The detail frame renders header and breadcrumb skeletons immediately while hidden
/// section pages remain mounted to initiate loading.</summary>
public class ServerDetailLayoutLoadingTests : BunitContext
{
    public ServerDetailLayoutLoadingTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.AddScoped(sp => new ServerDetailLoader(
            sp.GetRequiredService<ApiClient>(),
            sp.GetRequiredService<HubConnectionFactory>(),
            sp.GetRequiredService<NavigationManager>(),
            NullLogger<ServerDetailLoader>.Instance));
    }

    private IRenderedComponent<ServerDetailLayout> RenderOnServicesPage(ServerDetailDto? server = null, bool loadCompleted = false)
    {
        // The trail is route-derived until the layout names the server: let OE's trail service see the route.
        Services.GetRequiredService<OmniBreadcrumbService>();
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/servers/5/services");
        var loader = Services.GetRequiredService<ServerDetailLoader>();
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.Server))!.SetValue(loader, server);
        typeof(ServerDetailLoader).GetProperty(nameof(ServerDetailLoader.InitialLoadCompleted))!.SetValue(loader, loadCompleted);
        return Render<ServerDetailLayout>(parameters => parameters
            .Add(layout => layout.Body, body => body.AddMarkupContent(0, "<div id=\"body\">body</div>")));
    }

    private static ServerDetailDto Server() => new()
    {
        Id = 5,
        Name = "mail-srv",
        Hostname = "10.0.0.5",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        AgentVersion = "1.0.1552",
        Tags = [],
        Services = []
    };

    [Fact]
    public void WhileLoading_TheServerSegmentIsASkeleton_NeverAnEmptySegment()
    {
        var cut = RenderOnServicesPage();

        var items = cut.FindAll(".omni-page-header__trail .omni-breadcrumb__item");
        Assert.Equal(3, items.Count);
        Assert.Contains("Servers", items[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("omni-page-header__crumb-loading", items[1].ClassList);
        Assert.NotNull(items[1].QuerySelector(".omni-skeleton"));
        Assert.Contains("Services", items[2].TextContent, StringComparison.Ordinal);
        Assert.All(items, item => Assert.True(item.TextContent.Trim().Length > 0 || item.QuerySelector(".omni-skeleton") is not null));
    }

    [Fact]
    public void WhileLoading_ASkeletonStandsForTheContent_AndTheSectionStaysMountedButHidden()
    {
        var cut = RenderOnServicesPage();

        Assert.Equal(OmniDetailState.Loading, cut.Instance.DetailState);
        Assert.Equal("true", cut.Find(".omni-detail-shell").GetAttribute("aria-busy"));
        Assert.NotNull(cut.Find(".omni-detail-shell__loading.omni-skeleton"));
        Assert.Empty(cut.FindAll(".aetheus-loader"));
        var content = cut.Find(".omni-detail-shell__content");
        Assert.True(content.HasAttribute("hidden"));
        Assert.NotNull(content.QuerySelector("#body"));
        // The server's own actions wait for the server.
        Assert.Empty(cut.FindAll(".omni-page-header__actions"));
    }

    [Fact]
    public void OnceLoaded_TheTrailNamesTheServer_AndTheContentShows()
    {
        var cut = RenderOnServicesPage(Server(), loadCompleted: true);

        Assert.Equal(OmniDetailState.Found, cut.Instance.DetailState);
        var items = cut.FindAll(".omni-page-header__trail .omni-breadcrumb__item");
        Assert.Contains("mail-srv", items[1].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".omni-page-header__crumb-loading"));
        Assert.Empty(cut.FindAll(".omni-detail-shell__loading"));
        Assert.False(cut.Find(".omni-detail-shell__content").HasAttribute("hidden"));
    }

    [Fact]
    public void AnUnknownServer_GivesWayToTheNotFoundState_WithAWayBackToTheServers()
    {
        var cut = RenderOnServicesPage(loadCompleted: true);

        Assert.Equal(OmniDetailState.NotFound, cut.Instance.DetailState);
        Assert.Contains("ServerNotFound", cut.Find(".omni-detail-shell__not-found").TextContent, StringComparison.Ordinal);
        Assert.Contains("BackToServers", cut.Find(".omni-detail-shell__not-found button").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".omni-page-header"));
        Assert.True(cut.Find(".omni-detail-shell__content").HasAttribute("hidden"));
    }
}
