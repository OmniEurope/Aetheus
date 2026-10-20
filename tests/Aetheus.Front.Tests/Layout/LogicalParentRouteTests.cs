// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;

namespace Aetheus.Front.Tests.Layout;

/// <summary>Recette R3-29: the one answer to "where does Back lead when the history holds nothing of
/// the application", shared by the back button and the parent entry seeded at start.</summary>
public sealed class LogicalParentRouteTests
{
    [Theory]
    [InlineData("servers/5/mail", "/servers/5/overview")]
    [InlineData("servers/5/services", "/servers/5/overview")]
    [InlineData("servers/5/services?tab=units#top", "/servers/5/overview")]
    // The server's own crumb links to its overview: on the overview it is the page itself, so the list.
    [InlineData("servers/5/overview", "/servers")]
    [InlineData("servers/5", "/servers")]
    [InlineData("projects/7/pipelines", "/projects/7/overview")]
    [InlineData("pipelines/12/template/extract", "/pipelines/12")]
    [InlineData("pipelines/runs/1312", "/pipelines")]
    [InlineData("admin/settings", "/admin")]
    [InlineData("audit", "/admin")]
    // A top-level page has no ancestor link: the dashboard is the parent.
    [InlineData("servers", "/")]
    [InlineData("alerts?status=open", "/")]
    public void Of_RouteTrail_IsTheNearestAncestorLinkToAnotherPage(string path, string expected)
    {
        Assert.Equal(expected, LogicalParentRoute.Of(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("login")]
    [InlineData("login?returnUrl=%2Fservers%2F5%2Fmail")]
    [InlineData("account/change-password")]
    public void Of_DashboardAndSignInScreens_HaveNoParent(string path)
    {
        Assert.Null(LogicalParentRoute.Of(path));
    }

    [Fact]
    public void Of_PageTrail_WinsOverTheRouteTrail()
    {
        BreadcrumbItem[] trail =
        [
            new("Projects", "/projects"),
            new("Toto", "/projects/42/overview"),
            new("Pipelines", "/pipelines?projectId=42"),
            new("Deploy"),
        ];

        Assert.Equal("/pipelines?projectId=42", LogicalParentRoute.Of("pipelines/9", trail));
    }

    [Fact]
    public void Of_PageTrail_SkipsAnAncestorThatIsThePageItselfAndAnUnsafeLink()
    {
        BreadcrumbItem[] trail =
        [
            new("Servers", "/servers"),
            new("web-01", "//evil.example/servers"),
            new("web-01", "/servers/5/overview"),
            new("Overview"),
        ];

        Assert.Equal("/servers", LogicalParentRoute.Of("/servers/5/overview/", trail));
    }

    [Fact]
    public void Of_PageTrailWithoutAnyLink_FallsBackToTheDashboard()
    {
        Assert.Equal("/", LogicalParentRoute.Of("help/getting-started", [new BreadcrumbItem("Getting started")]));
    }
}
