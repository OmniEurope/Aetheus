// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

public class PipelineDependencyGridTests : BunitContext
{
    public PipelineDependencyGridTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void ParentRow_ExpandsChildrenInProvidedExecutionOrder()
    {
        var item = new PipelineDependencyDto
        {
            Id = 1,
            Name = "release",
            References =
            [
                new PipelineDependencyReferenceDto(2, "ci"),
                new PipelineDependencyReferenceDto(3, "qa"),
                new PipelineDependencyReferenceDto(4, "deploy")
            ]
        };
        var cut = RenderGrid(item);

        cut.Find("td[data-omni-control='expand'] button[aria-expanded]").Click();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.True(markup.IndexOf(">ci<", StringComparison.Ordinal) < markup.IndexOf(">qa<", StringComparison.Ordinal));
            Assert.True(markup.IndexOf(">qa<", StringComparison.Ordinal) < markup.IndexOf(">deploy<", StringComparison.Ordinal));
            Assert.Contains("pipeline-relation-list-execution", markup);
            Assert.Equal(3, cut.FindAll(".pipeline-relation-marker").Count);
            Assert.All(cut.FindAll(".pipeline-relation-card"), card => Assert.Contains("pipeline-relation-name", card.InnerHtml));
        });
    }

    /// <summary>The list follows the pipelines and entities hubs: every live refresh hands the grid freshly
    /// read items. A row the reader opened stays open across it (E2E 2026-11-02: the open detail vanished
    /// under the click when a refresh landed, the row went back to "Expand row").</summary>
    [Fact]
    public void OpenRow_StaysOpen_WhenALiveRefreshHandsTheGridFreshlyReadItems()
    {
        static PipelineDependencyDto Read(int id, string name) => new()
        {
            Id = id,
            Name = name,
            References = [new PipelineDependencyReferenceDto(9, "deploy")]
        };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { Read(1, "release"), Read(2, "qa") })
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask));

        cut.FindAll("td[data-omni-control='expand'] button[aria-expanded]")[0].Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".pipeline-relation-list-execution")));

        cut.Render(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { Read(1, "release"), Read(2, "qa") }));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".pipeline-relation-list-execution")));
        Assert.Equal("true", cut.FindAll("td[data-omni-control='expand'] button[aria-expanded]")[0].GetAttribute("aria-expanded"));
    }

    /// <summary>One list, the chevron decided per row. A leaf (even one with parents) has
    /// none.</summary>
    [Fact]
    public void MixedRows_ShowTheChevronOnlyOnRowsWithChildren()
    {
        var items = new List<PipelineDependencyDto>
        {
            new() { Id = 1, Name = "release", References = [new PipelineDependencyReferenceDto(4, "deploy")] },
            new() { Id = 4, Name = "deploy", Parents = [new PipelineDependencyReferenceDto(1, "release")] },
            new() { Id = 5, Name = "lint" }
        };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask));

        var rows = cut.FindAll("tr[data-omni-row-index]");
        Assert.Equal(3, rows.Count);
        Assert.NotNull(rows.Single(row => row.TextContent.Contains("release", StringComparison.Ordinal)
                && !row.TextContent.Contains("deploy", StringComparison.Ordinal))
            .QuerySelector("td[data-omni-control='expand'] button[aria-expanded]"));
        Assert.Single(cut.FindAll("td[data-omni-control='expand'] button[aria-expanded]"));
        Assert.True(PipelineDependencyGrid.HasChildren(items[0]));
        Assert.False(PipelineDependencyGrid.HasChildren(items[1]));
        Assert.False(PipelineDependencyGrid.HasChildren(items[2]));
    }

    /// <summary>Run and the star stay on the row; Edit sits in the row's "..." menu.</summary>
    [Fact]
    public void RowActions_KeepRunAndStarVisible_AndEditLivesInTheMenu()
    {
        var cut = RenderGrid(new PipelineDependencyDto { Id = 4, Name = "deploy" });

        Assert.Single(cut.FindAll("button[aria-label='AddPipelineToFavorites']"));
        Assert.Contains(cut.FindAll("button"), button => button.Names().Contains("Run", StringComparison.Ordinal) && !button.Names().Contains("LastRun", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[role='menuitem']"));

        cut.Find("button[aria-label='MoreActions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("Edit", StringComparison.Ordinal)).Click();

        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/pipelines/4?tab=edit", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void RunButton_InvokesCallbackWithPipelineId()
    {
        (int Id, string? Mode)? invocation = null;
        var item = new PipelineDependencyDto { Id = 17, Name = "release" };
        Func<int, string?, Task> run = (id, mode) => { invocation = (id, mode); return Task.CompletedTask; };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { item })
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, run)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask)
            .Add(component => component.CanWrite, true));

        cut.FindAll("button").Single(button => button.Names().Contains("Run", StringComparison.Ordinal) && !button.Names().Contains("LastRun", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(invocation);
            Assert.Equal(17, invocation.Value.Id);
            Assert.Null(invocation.Value.Mode);
        });
    }

    [Fact]
    public void RunButton_StaysBusyForTheWholeLaunch_ThenComesBack()
    {
        var launch = new TaskCompletionSource();
        var calls = 0;
        var item = new PipelineDependencyDto { Id = 17, Name = "release" };
        Func<int, string?, Task> run = (_, _) => { calls++; return launch.Task; };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { item })
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, run)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask)
            .Add(component => component.CanWrite, true));
        AngleSharp.Dom.IElement RunButton() =>
            cut.FindAll("button").Single(button => button.Names().Contains("Run", StringComparison.Ordinal) && !button.Names().Contains("LastRun", StringComparison.Ordinal));

        RunButton().Click();

        cut.WaitForAssertion(() => Assert.Equal("true", RunButton().GetAttribute("aria-busy")));
        Assert.False(RunButton().HasAttribute("disabled"));
        Assert.NotNull(cut.Find(".btn-busy"));
        Assert.Equal(1, calls);

        launch.SetResult();

        cut.WaitForAssertion(() => Assert.Null(RunButton().GetAttribute("aria-busy")));
        Assert.Empty(cut.FindAll(".btn-busy"));
    }

    [Fact]
    public void FavoriteButton_UsesFilledStarAndRequestsRemoval()
    {
        (int Id, bool IsFavorite)? invocation = null;
        var item = new PipelineDependencyDto { Id = 17, Name = "release" };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { item })
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (id, favorite) =>
            {
                invocation = (id, favorite);
                return Task.CompletedTask;
            })
            .Add(component => component.FavoritePipelineIds, new HashSet<int> { 17 }));

        var button = cut.Find("button[aria-label='RemovePipelineFromFavorites']");
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.StarFilled);

        button.Click();

        Assert.Equal((17, false), invocation);
    }

    [Fact]
    public void NonFavoriteButton_UsesOutlinedStarAndRequestsAddition()
    {
        (int Id, bool IsFavorite)? invocation = null;
        var item = new PipelineDependencyDto { Id = 18, Name = "deploy" };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { item })
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (id, favorite) =>
            {
                invocation = (id, favorite);
                return Task.CompletedTask;
            }));

        var button = cut.Find("button[aria-label='AddPipelineToFavorites']");
        Assert.Contains(cut.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Star);

        button.Click();

        Assert.Equal((18, true), invocation);
    }

    [Fact]
    public void GlobalGrid_GroupsRowsByProject()
    {
        var items = new List<PipelineDependencyDto>
        {
            new() { Id = 1, Name = "ci", ProjectName = "Alpha" },
            new() { Id = 2, Name = "qa", ProjectName = "Beta" }
        };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask)
            .Add(component => component.GroupByProject, true)
            .Add(component => component.Virtualize, true));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Alpha", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Beta", cut.Markup, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("td[data-omni-control='expand'] button[aria-expanded]"));
            var dataGrid = cut.FindComponent<OmniDataGrid<PipelineDependencyDto>>().Instance;
            Assert.Equal(OmniDataGridScrollMode.Virtual, dataGrid.ScrollMode);
            Assert.False(dataGrid.AllowGrouping);
        });
    }

    [Fact]
    public void Rows_RenderFiveRecentRunStatusDots()
    {
        var item = new PipelineDependencyDto
        {
            Id = 5,
            Name = "ci",
            ProjectId = 8,
            RecentRuns =
            [
                new PipelineRunSummaryDto { Id = 15, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Running },
                new PipelineRunSummaryDto { Id = 14, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Failed },
                new PipelineRunSummaryDto { Id = 13, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Success },
                new PipelineRunSummaryDto { Id = 12, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Cancelled },
                new PipelineRunSummaryDto { Id = 11, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Pending }
            ]
        };

        var cut = RenderGrid(item);

        var dots = cut.FindAll(".pipeline-run-history .pipeline-run-dot");
        Assert.Equal(5, dots.Count);
        Assert.Equal("/pipelines/runs/11?projectId=8", dots[0].GetAttribute("href"));
        Assert.Equal("/pipelines/runs/15?projectId=8", dots[^1].GetAttribute("href"));
        Assert.Contains("pipeline-run-dot-running", dots[^1].ClassList);
    }

    [Fact]
    public void ModelStatus_LinksModelAndOffersDirectUpdate()
    {
        var item = new PipelineDependencyDto { Id = 17, Name = "release" };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { item })
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask)
            .Add(component => component.CanWrite, true)
            .Add(component => component.FleetAvailable, true)
            .Add(component => component.FleetItems, new Dictionary<int, PipelineFleetItemDto>
            {
                [17] = new()
                {
                    PipelineId = 17,
                    TemplateId = 8,
                    TemplateName = "release-model",
                    PinnedVersion = 2,
                    LatestVersion = 3,
                    Freshness = PipelineFleetFreshness.Outdated
                }
            }));

        Assert.Contains("href=\"/templates/8\"", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("release-model", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Outdated", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("v2", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("v@pinnedVersion", cut.Markup, StringComparison.Ordinal);

        // Model updates belong in the row menu.
        Assert.DoesNotContain(cut.FindAll("button"), button => button.Names().Contains("Update", StringComparison.Ordinal));
        cut.Find("button[aria-label='MoreActions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("Update", StringComparison.Ordinal)).Click();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/pipelines/17/template/update/3", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineWithoutModel_IsExplicitlyAutonomous()
    {
        var item = new PipelineDependencyDto { Id = 18, Name = "standalone" };
        var cut = Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { item })
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask)
            .Add(component => component.FleetAvailable, true)
            .Add(component => component.FleetItems, new Dictionary<int, PipelineFleetItemDto>
            {
                [18] = new() { PipelineId = 18, Freshness = PipelineFleetFreshness.OffCatalog }
            }));

        Assert.Contains("AutonomousPipeline", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/templates/", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentRuns_ShowTheGradeOfTheNewestRunOnly()
    {
        // The pipelines table carries the grade the run grids show.
        var graded = RenderGrid(new PipelineDependencyDto
        {
            Id = 1,
            Name = "candidate",
            RecentRuns =
            [
                new PipelineRunSummaryDto { Id = 9, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Success, GateGrade = Aetheus.Shared.Components.Analysis.AnalysisGrade.C },
                new PipelineRunSummaryDto { Id = 8, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Success, GateGrade = Aetheus.Shared.Components.Analysis.AnalysisGrade.A }
            ]
        });
        var badge = Assert.Single(graded.FindAll(".grade-badge"));
        Assert.Equal("C", badge.TextContent.Trim());

        var ungraded = RenderGrid(new PipelineDependencyDto
        {
            Id = 2,
            Name = "deploy",
            RecentRuns = [new PipelineRunSummaryDto { Id = 7, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Success }]
        });
        Assert.Empty(ungraded.FindAll(".grade-badge"));
    }

    private IRenderedComponent<PipelineDependencyGrid> RenderGrid(PipelineDependencyDto item) =>
        Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineDependencyDto> { item })
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask)
            .Add(component => component.CanWrite, true));

    /// <summary>R-33: the date of the newest run (summaries are newest first) is written in the row, no hover needed.</summary>
    [Fact]
    public void Rows_ShowTheDateOfTheLatestRunNextToTheHistory()
    {
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        var newest = new DateTime(2026, 11, 4, 22, 30, 0, DateTimeKind.Local);
        var older = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Local);

        var cut = RenderGrid(new PipelineDependencyDto
        {
            Id = 5,
            Name = "ci",
            RecentRuns =
            [
                new PipelineRunSummaryDto { Id = 15, StartedAt = newest, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Success },
                new PipelineRunSummaryDto { Id = 14, StartedAt = older, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Failed }
            ]
        });

        var date = cut.Find("[data-testid='pipeline-last-run']");
        Assert.Equal(newest.ToString("g"), date.TextContent);
        Assert.Empty(cut.FindAll("[data-testid='pipeline-last-run-never']"));
        Assert.Contains("Last Run", cut.Markup);
    }

    /// <summary>R-33: a pipeline that never ran says so, in both resource files.</summary>
    [Fact]
    public void Rows_SayNeverRun_WhenThePipelineHasNoRun()
    {
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());

        var cut = RenderGrid(new PipelineDependencyDto { Id = 6, Name = "fresh" });

        Assert.Equal("Never run", cut.Find("[data-testid='pipeline-last-run-never']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='pipeline-last-run']"));
        Assert.Equal("Jamais lancé", ResxLocalizer.French()["PipelineNeverRun"].Value);
        Assert.Equal("Dernière exécution", ResxLocalizer.French()["LastRun"].Value);
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        // The name link, the favourite button, the run split button and the run history dots
        // (PipelineRunHistory) all sit in the clickable row.
        var cut = RenderGrid(new PipelineDependencyDto
        {
            Id = 5,
            Name = "ci",
            ProjectId = 8,
            RecentRuns = [new PipelineRunSummaryDto { Id = 15, Status = Aetheus.Shared.Components.Pipelines.PipelineStatus.Success }]
        });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        Assert.NotEmpty(cut.FindAll(".pipeline-run-history a.pipeline-run-dot"));
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 4);
    }
}
