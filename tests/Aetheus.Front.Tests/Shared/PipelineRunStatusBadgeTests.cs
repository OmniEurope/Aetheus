// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Releases;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;
using ProjectsPage = Aetheus.Front.Components.Projects.Projects;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-37 (user decision 2026-10-24): every run-status badge is the one shared
/// <see cref="PipelineRunStatusBadge"/>, with the status icon before the localized name.</summary>
public sealed class PipelineRunStatusBadgeTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunStatusBadgeTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static void AssertBadgeWithIcon(AngleSharp.Dom.IElement badge, PipelineStatus status)
    {
        Assert.NotEmpty(badge.QuerySelectorAll(".omni-icon"));
        Assert.Contains($"Enum_PipelineStatus_{status}", badge.TextContent, StringComparison.Ordinal);
    }

    private static AngleSharp.Dom.IElement BadgeOf<TComponent>(IRenderedComponent<TComponent> cut, PipelineStatus status)
        where TComponent : Microsoft.AspNetCore.Components.IComponent =>
        Assert.Single(cut.FindAll(".omni-badge"), badge => badge.TextContent.Contains($"Enum_PipelineStatus_{status}", StringComparison.Ordinal));

    [Fact]
    public void EveryPipelineStatus_HasAnIcon()
    {
        foreach (var status in Enum.GetValues<PipelineStatus>())
            Assert.True(PipelineHelper.GetRunIcon(status) is not null, $"{status} has no run icon.");
    }

    [Fact]
    public void EveryPipelineStatus_HasItsOwnIcon_PartialAndRefusedAreApart()
    {
        Assert.NotEqual(PipelineHelper.GetRunIcon(PipelineStatus.Partial), PipelineHelper.GetRunIcon(PipelineStatus.Rejected));

        var icons = Enum.GetValues<PipelineStatus>().Select(PipelineHelper.GetRunIcon).ToList();
        Assert.Equal(icons.Count, icons.Distinct().Count());
    }

    [Theory]
    [InlineData(PipelineStatus.Pending)]
    [InlineData(PipelineStatus.Running)]
    [InlineData(PipelineStatus.WaitingForApproval)]
    [InlineData(PipelineStatus.Success)]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    [InlineData(PipelineStatus.Partial)]
    [InlineData(PipelineStatus.RolledBack)]
    [InlineData(PipelineStatus.Rejected)]
    [InlineData(PipelineStatus.AwaitingResume)]
    public void TheBadge_ShowsTheStatusIconToneAndName(PipelineStatus status)
    {
        var cut = Render<PipelineRunStatusBadge>(parameters => parameters
            .Add(badge => badge.Status, status)
            .Add(badge => badge.Width, OmniBadgeWidth.Medium)
            .AddUnmatched("data-testid", "run-status"));

        var badge = cut.Find("[data-testid='run-status']");
        Assert.Contains("omni-badge", badge.ClassName, StringComparison.Ordinal);
        AssertBadgeWithIcon(badge, status);
        Assert.Contains(PipelineHelper.GetRunBadge(status).ToString().ToLowerInvariant(), badge.ClassName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownStatus_CarriesTheQuestionIcon_BecauseATextBadgeAlwaysHasOne()
    {
        var cut = Render<PipelineRunStatusBadge>(parameters => parameters.Add(badge => badge.Status, (PipelineStatus)999));

        Assert.Single(cut.FindAll(".omni-badge .omni-icon"));
        Assert.Single(cut.FindAll(".omni-badge"));
    }

    [Fact]
    public void PipelineRunsGrid_BothBadgePlaces_CarryTheIcon()
    {
        var running = new PipelineRunDto { Id = 1, PipelineId = 1, PipelineName = "ci", Status = PipelineStatus.Running };
        var done = new PipelineRunDto { Id = 2, PipelineId = 1, PipelineName = "ci", Status = PipelineStatus.Success };
        var cut = Render<PipelineRunsGrid>(parameters => parameters.Add(component => component.Items, new List<PipelineRunDto> { running, done }));

        cut.WaitForAssertion(() => AssertBadgeWithIcon(BadgeOf(cut, PipelineStatus.Success), PipelineStatus.Success));
    }

    [Fact]
    public void ProjectsPage_LastRunBadge_CarriesTheIcon()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto
                {
                    Id = 1,
                    Name = "Operational",
                    Status = ProjectStatus.Active,
                    LastRunId = 52,
                    LastRunName = "Deploy",
                    LastRunStatus = PipelineStatus.Rejected,
                    LastRunAt = new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc)
                }
            ],
            TotalCount = 1
        });

        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Operational"));

        var badges = cut.FindAll(".project-activity-run .omni-badge");
        Assert.NotEmpty(badges);
        Assert.All(badges, badge => AssertBadgeWithIcon(badge, PipelineStatus.Rejected));
    }

    [Fact]
    public void ProjectsPage_DenseListLastRunBadge_CarriesTheIcon()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items =
            [
                new ProjectDto
                {
                    Id = 1,
                    Name = "Dense",
                    Status = ProjectStatus.Active,
                    LastRunId = 52,
                    LastRunName = "Deploy",
                    LastRunStatus = PipelineStatus.Partial,
                    LastRunAt = new DateTime(2026, 7, 30, 9, 5, 0, DateTimeKind.Utc)
                }
            ],
            TotalCount = 1
        });

        var cut = Render<ProjectsPage>();
        cut.WaitForState(() => cut.Markup.Contains("Dense"));
        cut.Find("button[title='DenseView']").Click();

        var badge = Assert.Single(cut.FindAll(".project-dense-activity .project-activity-run .omni-badge"));
        AssertBadgeWithIcon(badge, PipelineStatus.Partial);
    }

    [Fact]
    public void ReleasePickerGrid_RunStatusBadge_CarriesTheIcon()
    {
        var release = new ReleaseDto
        {
            Id = 1,
            ProjectId = 1,
            Version = "c-014f63d4",
            Status = ReleaseStatus.Published,
            PipelineRunStatus = PipelineStatus.Failed
        };
        var cut = Render<ReleasePickerGrid>(parameters => parameters
            .Add(grid => grid.Releases, new List<ReleaseDto> { release })
            .Add(grid => grid.TotalCount, 1));
        cut.Find("[data-testid='release-picker-show-all']").Click();

        cut.WaitForAssertion(() => AssertBadgeWithIcon(cut.Find("[data-testid='release-picker-run-status']"), PipelineStatus.Failed));
    }

    [Fact]
    public void ReleaseCreatorRows_CreatorRunBadge_CarriesTheIcon()
    {
        var provenance = new ReleaseProvenanceDto
        {
            CreatedBy = new ReleaseRunRefDto { RunId = 9, PipelineId = 3, PipelineName = "build", Status = PipelineStatus.Partial }
        };
        var cut = Render<ReleaseCreatorRows>(parameters => parameters
            .Add(rows => rows.Release, new ReleaseDto { Id = 1 })
            .Add(rows => rows.Provenance, provenance));

        AssertBadgeWithIcon(BadgeOf(cut, PipelineStatus.Partial), PipelineStatus.Partial);
    }

    [Fact]
    public void ReleaseProvenancePanel_UseBadge_CarriesTheIcon()
    {
        var provenance = new ReleaseProvenanceDto
        {
            Uses =
            [
                new ReleaseUseDto
                {
                    Kind = ReleaseUseKind.Deploy,
                    Run = new ReleaseRunRefDto { RunId = 12, PipelineId = 4, PipelineName = "deploy", Status = PipelineStatus.RolledBack }
                }
            ]
        };
        var cut = Render<ReleaseProvenancePanel>(parameters => parameters.Add(panel => panel.Provenance, provenance));

        AssertBadgeWithIcon(BadgeOf(cut, PipelineStatus.RolledBack), PipelineStatus.RolledBack);
    }

    [Fact]
    public void PipelineRunLineageDialog_FollowUpBadges_CarryTheIcon()
    {
        var runs = new List<PipelineRunLinkDto>
        {
            new() { RunId = 101, PipelineId = 9, PipelineName = "deliver", BuildNumber = 1, Status = PipelineStatus.Cancelled }
        };
        var cut = Render<PipelineRunLineageDialog>(parameters => parameters.Add(dialog => dialog.Runs, runs));

        AssertBadgeWithIcon(BadgeOf(cut, PipelineStatus.Cancelled), PipelineStatus.Cancelled);
    }

    [Fact]
    public void PipelineRunLineageTile_FollowUpBadges_CarryTheIcon()
    {
        _handler.SetJsonResponse("api/pipelines/runs/7/lineage", new PipelineRunLineageDto
        {
            Downstream = [new PipelineRunLinkDto { RunId = 101, PipelineId = 9, PipelineName = "deliver", BuildNumber = 1, Status = PipelineStatus.Success }]
        });
        var run = new PipelineRunDto { Id = 7, PipelineId = 5, PipelineName = "candidate", Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };

        var cut = Render<PipelineRunLineageTile>(parameters => parameters.Add(tile => tile.Run, run));

        cut.WaitForAssertion(() => AssertBadgeWithIcon(BadgeOf(cut, PipelineStatus.Success), PipelineStatus.Success));
    }

    [Fact]
    public void PipelineRunHierarchy_RowBadges_CarryTheIcon()
    {
        var items = new List<PipelineRunTableItem>
        {
            new() { RunId = 1, PipelineId = 1, PipelineName = "ci", Status = PipelineStatus.AwaitingResume }
        };
        var cut = Render<PipelineRunHierarchy>(parameters => parameters.Add(tree => tree.Items, items));

        AssertBadgeWithIcon(BadgeOf(cut, PipelineStatus.AwaitingResume), PipelineStatus.AwaitingResume);
    }

    [Fact]
    public void PipelineRunPage_StatusTile_CarriesTheIcon()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
        _handler.SetJsonResponse("api/pipelines/runs/77", new PipelineRunDto
        {
            Id = 77,
            PipelineId = 5,
            PipelineName = "Deliver",
            Status = PipelineStatus.Failed
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 77));

        cut.WaitForAssertion(() => AssertBadgeWithIcon(
            Assert.Single(cut.FindAll(".run-status-tile .omni-badge")), PipelineStatus.Failed));
    }
}
