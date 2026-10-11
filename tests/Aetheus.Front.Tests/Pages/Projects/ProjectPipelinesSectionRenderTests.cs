// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectPipelinesSectionRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectPipelinesSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/pipelines/dependencies", new PipelineDependencyGroupsDto());
        _handler.SetJsonResponse("api/pipelines/runs/recent", new List<PipelineRunDto>());
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        // Recette R1-34: the section says where the definitions are kept, from the project's repositories.
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
    }

    [Fact]
    public void Renders_EmptyPipelines()
    {
        var cut = Render<ProjectPipelinesSection>(p =>
            p.Add(x => x.Pipelines, new List<PipelineDto>()));
        // Loaded (empty) → the catalogue view opens on its empty state (R-120). Recette R-215: the view
        // switch and its counts sit in the page header, which this section alone does not render.
        Assert.Contains("Pipelines", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("NoPipelinesFound", cut.Markup));
    }

    [Fact]
    public void Renders_WithPipelines()
    {
        var cut = Render<ProjectPipelinesSection>(p =>
            p.Add(x => x.Pipelines, new List<PipelineDto>
            {
                new() { Id = 1, Name = "CI", LastRunStatus = PipelineStatus.Success },
                new() { Id = 2, Name = "CD", LastRunStatus = PipelineStatus.Running }
            }));
        // The section renders the shared list, on its catalogue view (R-120 / R-215).
        Assert.Contains("Pipelines", cut.Markup);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pipeline-catalog-panel")));
    }
}
