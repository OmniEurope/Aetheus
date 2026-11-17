// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.Sections;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R-23: the services page is the application management page. Its tables take the size of
/// their content up to ten rows then scroll, and packages and updates is one of its tabs.</summary>
public class ApplicationManagementPageTests : BunitContext
{
    public ApplicationManagementPageTests() =>
        BunitTestHelper.RegisterServices(this).SetJsonResponse(HttpMethod.Get, "security-updates", new ServerSecurityUpdatesDto { Known = true, PackageManagerPresent = true });

    private static ServiceInfoDto Svc(string name, bool installed, bool manageable = true) => new()
    {
        Name = name,
        Status = installed ? "active" : "inactive",
        IsRunning = installed,
        IsInstalled = installed,
        IsManageable = manageable,
        Type = ServiceType.Systemd
    };

    private static ServerDetailDto MakeServer(params ServiceInfoDto[] services) => new()
    {
        Id = 21,
        Name = "apps-srv",
        Hostname = "10.0.0.21",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = services.ToList(),
        PackageManagementAvailable = true
    };

    private IRenderedComponent<ServerServicesSection> RenderSection(ServerDetailDto server) =>
        Render<ServerServicesSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));

    private void Open(string url) =>
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo(url);

    [Fact]
    public void TheInstalledAndAvailableGrids_TakeTheSizeOfTheirContent_AndScrollAfterTenRows()
    {
        var cut = RenderSection(MakeServer(Svc("docker", installed: true)));

        var grids = cut.FindComponents<OmniDataGrid<ServiceInfoDto>>();

        Assert.Equal(2, grids.Count);
        Assert.All(grids, grid =>
        {
            Assert.Equal(AetheusGrid.TenRowsMaxHeight, grid.Instance.MaxHeight);
            Assert.False(grid.Instance.FillAvailableHeight, "a table no longer takes the whole remaining height");
        });
    }

    [Fact]
    public void TheSystemGrid_TakesTheSizeOfItsContent_AndScrollsAfterTenRows()
    {
        Open("/servers/21/services?tab=system");

        var cut = RenderSection(MakeServer(Svc("systemd-journald", installed: true, manageable: false)));

        var grid = Assert.Single(cut.FindComponents<OmniDataGrid<ServiceInfoDto>>());
        Assert.Equal(AetheusGrid.TenRowsMaxHeight, grid.Instance.MaxHeight);
        Assert.False(grid.Instance.FillAvailableHeight);
    }

    [Fact]
    public void PackagesAndUpdates_IsATabOfThePage()
    {
        Open("/servers/21/services?tab=updates");

        var cut = RenderSection(MakeServer(Svc("docker", installed: true)));

        Assert.Contains(cut.FindAll("[role='tab']"), tab => tab.TextContent.Contains("PatchTitle", StringComparison.Ordinal));
        Assert.Single(cut.FindComponents<ServerPatchSection>());
    }

    [Fact]
    public void TheFormerUpdatesUrl_RedirectsToTheUpdatesTabOfTheServicesPage()
    {
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        Render<Updates>(p => p.Add(x => x.Id, 21));

        Assert.EndsWith("/servers/21/services?tab=updates", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_IsNamedApplicationManagement_InItsBreadcrumb()
    {
        var items = BreadcrumbRouteResolver.Resolve("servers/21/services", key => key);

        Assert.Equal("ServerApplicationsManagement", items[^1].Text);
    }
}
