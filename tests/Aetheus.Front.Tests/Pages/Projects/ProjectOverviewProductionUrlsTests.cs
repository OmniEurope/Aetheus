// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-29: the Information card of the project overview lists the production addresses, one per line, each a link
/// opening in a new tab. They are the ones the project detail carries (the back end reads them from the latest
/// successful production deployment); a project without a production deployment shows none.</summary>
public sealed class ProjectOverviewProductionUrlsTests : BunitContext
{
    public ProjectOverviewProductionUrlsTests() => BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<ProjectOverviewSection> Overview(PipelineRunDeployedAddressDto? address)
    {
        var cut = Render<ProjectOverviewSection>(p => p.Add(x => x.Project, new ProjectDetailDto
        {
            Id = 1,
            Name = "Wordmorrow",
            Status = ProjectStatus.Active,
            ProductionAddress = address
        }));
        cut.WaitForAssertion(() => Assert.Contains("overview-info-card", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public void TheApplicationAndTheApi_AreTwoLabelledLinks_InNewTabs()
    {
        var cut = Overview(new PipelineRunDeployedAddressDto { AppUrl = "https://app.example.test", ApiUrl = "https://api.example.test" });

        var app = cut.Find("a.project-production-app-link");
        var api = cut.Find("a.project-production-api-link");
        Assert.Equal("https://app.example.test", app.GetAttribute("href"));
        Assert.StartsWith("https://app.example.test", app.TextContent.Trim(), StringComparison.Ordinal);
        Assert.Equal("_blank", app.GetAttribute("target"));
        Assert.Equal("https://api.example.test", api.GetAttribute("href"));
        Assert.Equal("_blank", api.GetAttribute("target"));
        Assert.Contains("noopener", api.GetAttribute("rel"));
        // One line each, named by its label: Application then API, both for the production.
        Assert.Contains("Application · Production", cut.Find(".project-production-app-url").TextContent, StringComparison.Ordinal);
        Assert.Contains("RunDeployedApi · Production", cut.Find(".project-production-api-url").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApplicationWithoutAnAddressOfItsOwnForTheApi_ShowsOnlyTheApplication()
    {
        var cut = Overview(new PipelineRunDeployedAddressDto { AppUrl = "https://app.example.test" });

        Assert.Single(cut.FindAll("a.project-production-app-link"));
        Assert.Empty(cut.FindAll("a.project-production-api-link"));
        Assert.Empty(cut.FindAll(".project-production-api-url"));
    }

    [Fact]
    public void WithoutAProductionDeployment_NoAddressIsShown()
    {
        var cut = Overview(null);

        Assert.Empty(cut.FindAll("a.project-production-app-link"));
        Assert.Empty(cut.FindAll(".project-production-app-url"));
    }
}
