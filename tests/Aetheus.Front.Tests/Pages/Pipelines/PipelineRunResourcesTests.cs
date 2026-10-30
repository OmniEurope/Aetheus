// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-37 (the libraries and vaults a run used, in the run page's parameters tile) and R-36 (the
/// overview Findings count includes the blocking tests).</summary>
public sealed class PipelineRunResourcesTests : BunitContext
{
    private const string Yaml = """
        name: aetheus-candidate
        variable_libraries:
          - shared-build
          - Shared-Build
          - registry
        vaults:
          - deploy-secrets
        requires:
          libraries: [registry, extra-library]
          vaults: [deploy-secrets]
        stages: []
        """;

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunResourcesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    [Fact]
    public void TheSnapshotNamesTheLibrariesAndVaultsOnceEach()
    {
        var used = PipelineRunResources.Of(Yaml);

        Assert.Equal(["shared-build", "registry", "extra-library"], used.Libraries);
        Assert.Equal(["deploy-secrets"], used.Vaults);
    }

    [Fact]
    public void TheSlugExpressionReadsAsTheRunsProjectLibrary()
    {
        const string generic = """
            name: w8-deploy-prod
            variable_libraries:
              - ${{ project.slug }}.share
              - ${{ project.slug }}.prod
            vaults:
              - ${{ project.slug }}.prod
            stages: []
            """;

        var used = PipelineRunResources.Of(generic, "W8", 12);

        Assert.Equal(["w8.share", "w8.prod"], used.Libraries);
        Assert.Equal(["w8.prod"], used.Vaults);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not: [valid")]
    public void ARunWithoutAReadableSnapshotNamesNothing(string? snapshot) =>
        Assert.True(PipelineRunResources.Of(snapshot).IsEmpty);

    [Fact]
    public void TheParametersTile_ListsTheLibrariesAndVaultsTheRunUsed()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2500", new PipelineRunDto
        {
            Id = 2500,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow,
            Parameters = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
            YamlSnapshot = Yaml,
            Steps = [new PipelineStepRunDto { Id = 1, StageName = "Build", StepName = "Build", Status = TaskExecutionStatus.Success }]
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2500));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".run-parameters-tile")));
        var tile = cut.Find(".run-parameters-tile");
        Assert.Contains("shared-build, registry, extra-library", tile.QuerySelector("[data-testid=run-used-libraries]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("deploy-secrets", tile.QuerySelector("[data-testid=run-used-vaults]")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateOverviewTile_CountsTheBlockingTestsWithTheFindings()
    {
        var gate = new AnalysisRunGateDto { PipelineRunId = 1, FindingCount = 2 };

        var cut = Render<PipelineRunGateOverview>(parameters => parameters
            .Add(component => component.Gate, gate)
            .Add(component => component.BlockingTestCount, 3));

        Assert.Contains("5", cut.Markup, StringComparison.Ordinal);
    }
}
