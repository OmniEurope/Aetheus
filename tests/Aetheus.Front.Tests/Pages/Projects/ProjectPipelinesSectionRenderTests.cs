// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

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
        // The section says where the definitions are kept, from the project's repositories.
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
    }

    [Fact]
    public void Renders_EmptyPipelines()
    {
        var cut = Render<ProjectPipelinesSection>(p =>
            p.Add(x => x.Pipelines, new List<PipelineDto>()));
        // An empty loaded catalogue shows its empty state; the view switch and counts belong to the
        // surrounding page header.
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
        // Render the shared list in catalogue view.
        Assert.Contains("Pipelines", cut.Markup);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pipeline-catalog-panel")));
    }

    [Fact]
    public async Task R3_68_TheNotConfiguredBanners_AreDrawnWithTheList_NotAfterIt()
    {
        var release = new TaskCompletionSource();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/setup/project-readiness", async _ =>
        {
            await release.Task;
            return new PipelineSetupReadinessDto
            {
                RepositoryInspected = true,
                InspectedBranch = "main",
                Checks = [new PipelineSetupReadinessCheckDto
                {
                    Kind = PipelineSetupReadinessKind.MissingAdapterScripts,
                    Severity = PipelineSetupReadinessSeverity.Blocking,
                    Items = [".pipeline/scripts/ci.sh"]
                }]
            };
        });

        var cut = Render<ProjectPipelinesSection>(p => p
            .Add(x => x.Pipelines, new List<PipelineDto>())
            .Add(x => x.ProjectId, 5));

        // The verdict is not read yet: no list, a loader, no banner.
        Assert.Empty(cut.FindAll(".pipeline-catalog-panel"));
        Assert.Empty(cut.FindAll("[data-testid='project-pipelines-readiness']"));
        Assert.NotEmpty(cut.FindComponents<AetheusLoader>());

        release.SetResult();

        // Banner and list appear together; the list never shows without the banner the verdict carries.
        cut.WaitForAssertion(() => Assert.Contains("NoPipelinesFound", cut.Markup));
        Assert.NotEmpty(cut.FindAll("[data-testid='project-pipelines-readiness']"));
        await Task.CompletedTask;
    }

    [Fact]
    public void ALiveConnectionThatNeverAnswers_DoesNotHoldTheListBack()
    {
        // The realtime hubs are a refresh channel: the catalogue is drawn from its HTTP reads, not after
        // the hubs have started (a negotiate that hangs kept the page on its loader for good).
        Services.AddSingleton<HubConnectionFactory>(sp => new BunitTestHelper.BlockingHubConnectionFactory(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetRequiredService<AuthStateProvider>()));
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/setup/project-readiness", new PipelineSetupReadinessDto());
        // The catalogue answers after the list has drawn its loader, as every read does in the browser.
        var catalogue = new TaskCompletionSource<PipelineDependencyGroupsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/dependencies", _ => catalogue.Task);

        var cut = Render<ProjectPipelinesSection>(p => p
            .Add(x => x.Pipelines, new List<PipelineDto>())
            .Add(x => x.ProjectId, 5));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.Contains("api/pipelines/dependencies")));
        Assert.DoesNotContain("NoPipelinesFound", cut.Markup);

        catalogue.SetResult(new PipelineDependencyGroupsDto());

        cut.WaitForAssertion(() => Assert.Contains("NoPipelinesFound", cut.Markup));
    }

    [Fact]
    public void R3_68_AnUnreadVerdict_DoesNotHoldTheListBack()
    {
        _handler.SetResponse(HttpMethod.Get, "api/pipelines/setup/project-readiness", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<ProjectPipelinesSection>(p => p
            .Add(x => x.Pipelines, new List<PipelineDto>())
            .Add(x => x.ProjectId, 5));

        cut.WaitForAssertion(() => Assert.Contains("NoPipelinesFound", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid='project-pipelines-readiness']"));
    }
}
