// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components.Sections;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-24: a table of what runs right now sits between the status chips and the catalogue of the
/// general, project and server pipeline lists, with a direct link to each run, and is gone when nothing runs.</summary>
public sealed class PipelinesRunningNowTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelinesRunningNowTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/pipelines/dependencies", new PipelineDependencyGroupsDto
        {
            Leaves = [new PipelineDependencyDto { Id = 7, Name = "Build", ProjectId = 3 }]
        });
        _handler.SetJsonResponse("api/pipelines/favorites", new PipelineFavoritesDto());
        _handler.SetJsonResponse("api/pipelines/fleet", new PaginatedResult<PipelineFleetItemDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos", Array.Empty<GitLightRepoDto>());
    }

    private static PipelineRunDto Run(int id, PipelineStatus status, DateTime? startedAt = null) => new()
    {
        Id = id,
        BuildNumber = id - 29,
        PipelineId = 7,
        PipelineName = "deploy-prod",
        Status = status,
        StartedAt = startedAt ?? new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc)
    };

    private IRenderedComponent<PipelinesList> RenderList(int? projectId = null, int? serverId = null, params PipelineRunDto[] recent) =>
        RenderListWith(projectId, serverId, recent, recent.Where(run => PipelineRunningNowRows.IsShown(run.Status)).ToList());

    private IRenderedComponent<PipelinesList> RenderListWith(
        int? projectId, int? serverId, IReadOnlyList<PipelineRunDto> recent, IReadOnlyList<PipelineRunDto> active)
    {
        _handler.SetJsonResponse("api/pipelines/runs/recent", recent.ToList());
        _handler.SetJsonResponse("api/pipelines/runs/active", active.ToList());
        var host = Render(builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), PageHeaderActions.SectionName);
            builder.CloseComponent();
            builder.OpenComponent<PipelinesList>(2);
            builder.AddAttribute(3, nameof(PipelinesList.ProjectId), projectId);
            builder.AddAttribute(4, nameof(PipelinesList.ServerId), serverId);
            builder.CloseComponent();
        });
        var list = host.FindComponent<PipelinesList>();
        list.WaitForAssertion(() => Assert.Contains("Build", list.Markup, StringComparison.Ordinal));
        return list;
    }

    [Fact]
    public void TheGeneralList_ShowsTheRunningRunWithALinkToIt_AndLeavesOutTheFinishedOnes()
    {
        var list = RenderList(null, null, Run(41, PipelineStatus.Running), Run(40, PipelineStatus.Success));

        var link = Assert.Single(list.FindAll("[data-testid=pipelines-running-now-link]"));
        Assert.Equal("/pipelines/runs/41", link.GetAttribute("href"));
        // R-8: the text is the number a person reads (41 - 29), the href keeps the technical key.
        Assert.Contains("#12", link.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("#41", link.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProjectList_ShowsTheTableToo_AndKeepsTheProjectInTheLink()
    {
        var run = Run(41, PipelineStatus.Running);
        run = run with { ProjectId = 3, ProjectName = "Aetheus" };

        var list = RenderList(3, null, run);

        var link = Assert.Single(list.FindAll("[data-testid=pipelines-running-now-link]"));
        Assert.Equal("/pipelines/runs/41?projectId=3", link.GetAttribute("href"));
    }

    [Fact]
    public void TheServerList_ShowsTheTableToo_AndKeepsTheServerInTheLink()
    {
        var list = RenderList(null, 9, Run(41, PipelineStatus.Running));

        var link = Assert.Single(list.FindAll("[data-testid=pipelines-running-now-link]"));
        Assert.Equal("/pipelines/runs/41?serverId=9", link.GetAttribute("href"));
    }

    [Fact]
    public void ARunningRunOlderThanTheTwentyMostRecent_StillShows_BecauseTheTableReadsTheActiveRuns()
    {
        // The recent list holds only finished runs (the long-running one fell out of its 20 latest);
        // the active list still has it.
        var list = RenderListWith(null, null,
            [Run(40, PipelineStatus.Success), Run(39, PipelineStatus.Failed)],
            [Run(11, PipelineStatus.WaitingForApproval)]);

        var link = Assert.Single(list.FindAll("[data-testid=pipelines-running-now-link]"));
        Assert.Equal("/pipelines/runs/11", link.GetAttribute("href"));
    }

    [Fact]
    public void WhenNothingRuns_NoTableIsDrawn()
    {
        var list = RenderList(null, null, Run(40, PipelineStatus.Success), Run(39, PipelineStatus.Failed));

        Assert.Empty(list.FindAll("[data-testid=pipelines-running-now]"));
    }

    [Fact]
    public void ARunWaitingForAPerson_LeadsTheRunningOnes_ThenTheLongestRunningFirst()
    {
        var early = new DateTime(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

        var rows = PipelineRunningNowRows.From(
        [
            Run(1, PipelineStatus.Running, late),
            Run(2, PipelineStatus.Success, early),
            Run(3, PipelineStatus.Running, early),
            Run(4, PipelineStatus.WaitingForApproval, late),
            Run(5, PipelineStatus.AwaitingResume, early)
        ]);

        Assert.Equal([5, 4, 3, 1], rows.Select(row => row.RunId));
    }
}
