// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Layout;

/// <summary>Feed OmniPageHeader through the shared breadcrumb services: the title is the leaf,
/// followed by the trail or a subtitle when no ancestor exists. AetheusBreadcrumbResolver supplies
/// route fallbacks.</summary>
public sealed class AppBreadcrumbTests : BunitContext
{
    public AppBreadcrumbTests() => BunitTestHelper.RegisterServices(this);

    private static string Trail(IRenderedComponent<OmniPageHeader> cut) =>
        cut.Find(".omni-page-header__trail nav").InnerHtml;

    private static string Title(IRenderedComponent<OmniPageHeader> cut) =>
        cut.Find("h1.omni-page-header__title").TextContent;

    [Fact]
    public void Renders_The_Route_Ancestors_Then_The_Title_As_The_Current_Item()
    {
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/pipelines/runs/1312");

        var cut = Render<OmniPageHeader>();

        var landmark = cut.Find(".omni-page-header__trail nav");
        Assert.False(string.IsNullOrWhiteSpace(landmark.GetAttribute("aria-label")));
        Assert.Equal("/pipelines", cut.Find(".omni-page-header__trail a").GetAttribute("href"));
        Assert.Contains("Pipelines", Trail(cut), StringComparison.Ordinal);
        // The route fallback names the run as still loading: OE holds its place with a placeholder, on
        // the title line and as the trail's last item, until the page names it.
        Assert.Single(cut.FindAll(".omni-page-header__title-loading"));

        Services.GetRequiredService<BreadcrumbService>().Set(
            new BreadcrumbItem("Pipelines", "/pipelines"), new BreadcrumbItem("PipelineRun #1312"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("PipelineRun #1312", Title(cut), StringComparison.Ordinal);
            Assert.Equal("PipelineRun #1312", cut.Find(".omni-page-header__trail [aria-current='page']").TextContent.Trim());
            Assert.Empty(cut.FindAll(".omni-page-header__crumb-loading"));
        });
    }

    [Fact]
    public void Renders_A_Skeleton_For_An_Ancestor_Still_Loading()
    {
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/servers/7/docker");

        var cut = Render<OmniPageHeader>();

        // "Servers / <the server, still loading> / Docker": the middle item is an ancestor whose name
        // arrives with the data, so it holds the trail's width instead of letting the row reflow.
        Assert.Single(cut.FindAll(".omni-page-header__crumb-loading"));
        Assert.Contains("Servers", Trail(cut), StringComparison.Ordinal);
        Assert.Contains("Docker", Title(cut), StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_Replaces_Old_Items_With_Destination_Fallback_Immediately()
    {
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/tasks/9");
        var cut = Render<OmniPageHeader>();
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();
        breadcrumb.Set(new BreadcrumbItem("Tasks", "/tasks"), new BreadcrumbItem("Loaded task"));

        nav.NavigateTo("/servers/7/docker");

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Loaded task", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Server #7", Trail(cut), StringComparison.Ordinal);
            Assert.Single(cut.FindAll(".omni-page-header__crumb-loading"));
        });

        breadcrumb.Set(
            new BreadcrumbItem("Servers", "/servers"),
            new BreadcrumbItem("web-01", "/servers/7/overview"),
            new BreadcrumbItem("Docker"));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".omni-page-header__crumb-loading"));
            Assert.Contains("web-01", Trail(cut), StringComparison.Ordinal);
            Assert.Equal("Docker", Title(cut));
        });
    }

    [Fact]
    public void Single_Item_Trail_Keeps_Line_Two_For_The_Route_Description()
    {
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();
        var cut = Render<OmniPageHeader>(parameters => parameters.Add(header => header.Subtitle, breadcrumb.Subtitle()));
        breadcrumb.Set(new BreadcrumbItem("Dashboard"));
        cut.Render(parameters => parameters.Add(header => header.Subtitle, breadcrumb.Subtitle()));

        // Keep the second header line with the page description when no breadcrumb ancestors exist,
        // maintaining stable content alignment.
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".omni-page-header__trail"));
            Assert.Empty(cut.FindAll(".omni-breadcrumb__item"));
            Assert.Equal("PageDescription_dashboard", cut.Find(".omni-page-header__trail-text").TextContent);
            Assert.Contains("Dashboard", Title(cut), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void An_Explicit_Title_Wins_Over_The_Trail_Leaf()
    {
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();
        breadcrumb.Set(new BreadcrumbItem("Projects", "/projects"), new BreadcrumbItem("Project #42"));

        var cut = Render<OmniPageHeader>(parameters => parameters.Add(header => header.Title, "Aetheus"));

        Assert.Equal("Aetheus", Title(cut));
        Assert.Contains("Projects", Trail(cut), StringComparison.Ordinal);
    }

    /// <summary>The route description stands in for a missing subtitle only when the page has no
    /// ancestor; with a trail, only the page's own subtitle shows, under the header.</summary>
    [Fact]
    public void The_Subtitle_Falls_Back_To_The_Route_Description_Only_Without_Ancestors()
    {
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();

        breadcrumb.Set(new BreadcrumbItem("Dashboard"));
        Assert.Equal("PageDescription_dashboard", breadcrumb.Subtitle());
        Assert.Equal("Mine", breadcrumb.Subtitle("Mine"));

        breadcrumb.Set(new BreadcrumbItem("Projects", "/projects"), new BreadcrumbItem("Project #42"));
        Assert.Null(breadcrumb.Subtitle());
        Assert.Equal("Mine", breadcrumb.Subtitle("Mine"));
        Assert.Equal("/projects", breadcrumb.ParentHref);
        Assert.Equal(["Projects", "Project #42"],
            Services.GetRequiredService<OmniBreadcrumbService>().Items.Select(entry => entry.Text));
    }
}
