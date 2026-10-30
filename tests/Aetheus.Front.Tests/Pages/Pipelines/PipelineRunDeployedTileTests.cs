// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-82: the deployed address is a tile of its own, with a link to the application and, when it has
/// an address of its own, to the API, each opening in a new tab. The addresses are the ones the run detail
/// carries (the back end reads them); a run without them shows no tile.</summary>
public sealed class PipelineRunDeployedTileTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunDeployedTileTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private IRenderedComponent<PipelineRunDeployedTile> Tile(PipelineRunDto run) =>
        Render<PipelineRunDeployedTile>(p => p.Add(x => x.Run, run));

    [Fact]
    public void ASucceededDeploymentWithTwoAddresses_LinksToTheApplicationAndTheApiInNewTabs()
    {
        var cut = Tile(new PipelineRunDto
        {
            Status = PipelineStatus.Success,
            DeployedAddress = new PipelineRunDeployedAddressDto { AppUrl = "https://app.example.test", ApiUrl = "https://api.example.test" }
        });

        var app = cut.Find("a.run-deployed-app-link");
        var api = cut.Find("a.run-deployed-api-link");
        Assert.Equal("https://app.example.test", app.GetAttribute("href"));
        Assert.Equal("_blank", app.GetAttribute("target"));
        Assert.Equal("https://api.example.test", api.GetAttribute("href"));
        Assert.Equal("_blank", api.GetAttribute("target"));
        Assert.Contains("noopener", api.GetAttribute("rel"));
    }

    [Fact]
    public void AnApplicationWithoutAnAddressOfItsOwnForTheApi_HasOnlyTheApplicationLink()
    {
        var cut = Tile(new PipelineRunDto
        {
            Status = PipelineStatus.Success,
            DeployedAddress = new PipelineRunDeployedAddressDto { AppUrl = "https://app.example.test" }
        });

        Assert.Equal("https://app.example.test", cut.Find("a.run-deployed-app-link").GetAttribute("href"));
        Assert.Empty(cut.FindAll("a.run-deployed-api-link"));
    }

    [Fact]
    public void ARunWithoutADeployedAddress_ShowsNoTile()
    {
        var cut = Tile(new PipelineRunDto { Status = PipelineStatus.Failed });

        Assert.Empty(cut.FindAll(".run-deployed-tile"));
        Assert.Empty(cut.FindAll("a"));
    }

    [Fact]
    public void TheRunPage_PlacesTheTileInTheOverviewGridOnlyForARunThatCarriesAnAddress()
    {
        _handler.SetJsonResponse("api/pipelines/runs/820", new PipelineRunDto
        {
            Id = 820,
            PipelineId = 5,
            PipelineName = "Deploy prod",
            Status = PipelineStatus.Success,
            DeployedAddress = new PipelineRunDeployedAddressDto { AppUrl = "https://app.example.test", ApiUrl = "https://api.example.test" }
        });
        _handler.SetJsonResponse("api/pipelines/runs/821", new PipelineRunDto
        {
            Id = 821,
            PipelineId = 5,
            PipelineName = "Deploy prod",
            Status = PipelineStatus.Failed
        });

        var deployed = Render<PipelineRun>(p => p.Add(x => x.RunId, 820));
        deployed.WaitForAssertion(() => Assert.Single(deployed.FindAll(".run-overview-tiles .run-deployed-tile")));
        Assert.Equal("https://app.example.test", deployed.Find(".run-overview-tiles a.run-deployed-app-link").GetAttribute("href"));

        var failed = Render<PipelineRun>(p => p.Add(x => x.RunId, 821));
        failed.WaitForAssertion(() => Assert.Contains("Deploy prod", failed.Markup));
        Assert.Empty(failed.FindAll(".run-deployed-tile"));
    }
}
