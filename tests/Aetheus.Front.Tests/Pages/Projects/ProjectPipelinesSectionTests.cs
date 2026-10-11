// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectPipelinesSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectPipelinesSectionTests()
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
    public void Renders_Loading_WhenPipelinesNull()
    {
        var cut = Render<ProjectPipelinesSection>(p =>
            p.Add(x => x.Pipelines, null)
             .Add(x => x.ProjectId, 1));
        // Null pipelines → only the branded loading indicator renders (no header/grid yet).
        Assert.Contains("aetheus-loader-logo", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyList_WithNoRecords()
    {
        var cut = Render<ProjectPipelinesSection>(p =>
            p.Add(x => x.Pipelines, new List<PipelineDto>())
             .Add(x => x.ProjectId, 5));
        Assert.Contains("Pipelines", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("NoPipelinesFound", cut.Markup));
        Assert.Single(cut.FindComponents<PipelinesList>());
    }

    /// <summary>R-124: the section's own New row is gone; the shared list's New (shown in the project
    /// header) still opens the setup wizard for this project.</summary>
    [Fact]
    public async Task NewPipeline_Navigates_ToSetupWizardWithProjectId()
    {
        var cut = Render<ProjectPipelinesSection>(p =>
            p.Add(x => x.Pipelines, new List<PipelineDto>())
             .Add(x => x.ProjectId, 7));
        Assert.Empty(cut.FindAll(".project-pipeline-new-button"));

        var list = cut.FindComponent<PipelinesList>();
        var method = typeof(PipelinesList).GetMethod("NewPipeline",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => method.Invoke(list.Instance, []));

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.Contains("pipelines/setup", nav.Uri);
        Assert.Contains("projectId=7", nav.Uri);
    }

    [Fact]
    public void PipelineWithLastRunStatus_ShowsBadge()
    {
        var pipelines = new List<PipelineDto>
        {
            new() { Id = 1, Name = "CI", TriggerType = PipelineTriggerType.Manual, LastRunStatus = PipelineStatus.Success }
        };
        var cut = Render<ProjectPipelinesSection>(p =>
            p.Add(x => x.Pipelines, pipelines)
             .Add(x => x.ProjectId, 1));
        Assert.Contains("Pipelines", cut.Markup);
        // Recette R-215: the view counts sit in the page header now; the section shows the catalogue view.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pipeline-catalog-panel")));
    }

    [Fact]
    public void LoadedSection_DelegatesBothDefinitionsAndRunsToSharedOverview()
    {
        var pipelines = new List<PipelineDto>
        {
            new() { Id = 2, Name = "Nightly", TriggerType = PipelineTriggerType.Schedule, LastRunStatus = null }
        };
        // Recette R-215: the views are picked in the project header, so the section is rendered next to
        // the header outlet the project layout gives it.
        var cut = Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), PipelinesList.HeaderActionsSection);
            builder.CloseComponent();
            builder.OpenComponent<ProjectPipelinesSection>(2);
            builder.AddAttribute(3, nameof(ProjectPipelinesSection.Pipelines), pipelines);
            builder.AddAttribute(4, nameof(ProjectPipelinesSection.ProjectId), 1);
            builder.CloseComponent();
        });
        var overview = cut.FindComponent<PipelinesList>();
        Assert.Equal(1, overview.Instance.ProjectId);
        cut.WaitForState(() => cut.FindAll(".pipeline-view-switch .omni-select-bar__item").Count == 2, TimeSpan.FromSeconds(2));
        cut.FindAll(".pipeline-view-switch .omni-select-bar__item")[1].Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<PipelineRunsGrid>()));
    }
}
