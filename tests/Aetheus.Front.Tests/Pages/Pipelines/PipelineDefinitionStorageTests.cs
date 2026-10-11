// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R1-34: the pipeline page and the project's pipelines say where a definition is kept.</summary>
public class PipelineDefinitionStorageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineDefinitionStorageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void NoGitSource_SaysTheDefinitionIsKeptInAetheus()
    {
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/12/source", HttpStatusCode.NoContent);

        var cut = Render<PipelineDefinitionStorage>(parameters => parameters.Add(component => component.PipelineId, 12));

        var note = cut.Find("[data-testid='pipeline-definition-storage']");
        Assert.Contains("PipelineDefinitionStoredInAetheus", note.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("a"));
    }

    [Fact]
    public void GitSource_NamesTheFileAndLinksTheRepository()
    {
        _handler.SetJsonResponse("api/pipelines/12/source", new PipelineSourceDto
        {
            RepositoryId = 4,
            Path = ".pipeline/deploy.yaml",
            Branch = "main",
            CloneUrl = "https://x/git/5/ats.git"
        });

        var cut = Render<PipelineDefinitionStorage>(parameters => parameters.Add(component => component.PipelineId, 12));

        var note = cut.Find("[data-testid='pipeline-definition-storage']");
        Assert.Contains("PipelineDefinitionStoredInGit", note.TextContent, StringComparison.Ordinal);
        Assert.Equal("/git-repositories/4", cut.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void UnreadSource_SaysNothing()
    {
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/12/source", HttpStatusCode.InternalServerError);

        var cut = Render<PipelineDefinitionStorage>(parameters => parameters.Add(component => component.PipelineId, 12));

        Assert.Empty(cut.FindAll("[data-testid='pipeline-definition-storage']"));
    }

    [Fact]
    public void ProjectWithoutInternalRepository_KeepsItsDefinitionsInAetheus()
    {
        // The mirror of an external repository attached as an additional source does not count.
        _handler.SetPaginatedJsonResponse("api/git/repos", new[]
        {
            new GitLightRepoDto { Id = 9, ProjectId = 5, Name = "upstream-mirror", IsAdditionalSource = true }
        });

        var cut = Render<ProjectPipelinesStorage>(parameters => parameters.Add(component => component.ProjectId, 5));

        Assert.Contains("ProjectPipelinesStoredInAetheus",
            cut.Find("[data-testid='project-pipelines-storage']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectWithAnInternalRepository_WritesItsDefinitionsToGit()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos", new[]
        {
            new GitLightRepoDto { Id = 3, ProjectId = 5, Name = "ats" }
        });

        var cut = Render<ProjectPipelinesStorage>(parameters => parameters.Add(component => component.ProjectId, 5));

        Assert.Contains("ProjectPipelinesStoredInGit",
            cut.Find("[data-testid='project-pipelines-storage']").TextContent, StringComparison.Ordinal);
    }
}
