// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Resources;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

public class PipelineRunsGridTests : BunitContext
{
    public PipelineRunsGridTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void LoadingIndicator_IsOeGridLoadingState_WithAccessibleStatus()
    {
        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.IsLoading, true));

        // PLAN-003 lot 15: one loading style everywhere. Since recette R-432 (user decision of 2026-09-29)
        // that style is OE's grid loading bar, not the Aetheus veil: the grid reads busy and its empty body
        // announces the load as a status region with a localized text, as the shared loader did.
        Assert.Equal("true", cut.Find(".omni-data-grid").GetAttribute("aria-busy"));
        Assert.Single(cut.FindAll("thead > tr.omni-data-grid__progress .omni-loading-bar--active"));
        var status = cut.Find("tr.omni-data-grid__pending [role='status']");
        Assert.False(string.IsNullOrWhiteSpace(status.TextContent), cut.Markup);
        Assert.Empty(cut.FindAll(".aetheus-loader"));
        Assert.Empty(cut.FindAll("[role='progressbar']"));
    }

    /// <summary>
    /// Recette R-373: the commit shows in the one short form (eight characters, the whole hash on hover)
    /// and leads to the commit's page in Aetheus when the run recorded it. An internal repository's URL
    /// is its smart-HTTP endpoint, so ".../commit/sha" on it served no page.
    /// </summary>
    [Fact]
    public void TheCommit_IsShortAndLeadsToItsPage()
    {
        var items = new List<PipelineRunDto>
        {
            new()
            {
                Id = 10,
                PipelineId = 1,
                PipelineName = "ci",
                Status = PipelineStatus.Success,
                CommitHash = "0123456789abcdef0123456789abcdef01234567",
                RepositoryUrl = "https://aetheus.test/git/1/aetheus.git",
                Commits = [new CommitLinkDto { Id = 33, Sha = "0123456789abcdef0123456789abcdef01234567" }]
            },
            new()
            {
                Id = 11,
                PipelineId = 1,
                PipelineName = "ci",
                Status = PipelineStatus.Success,
                CommitHash = "fedcba9876543210fedcba9876543210fedcba98",
                RepositoryUrl = "https://github.com/acme/demo.git"
            }
        };
        var cut = Render<PipelineRunsGrid>(parameters => parameters.Add(component => component.Items, items));

        cut.WaitForAssertion(() =>
        {
            var recorded = cut.Find(".short-id[title='0123456789abcdef0123456789abcdef01234567'] a.short-id__text");
            Assert.Equal("01234567", recorded.TextContent);
            Assert.Equal("/git/commits/33", recorded.GetAttribute("href"));
            Assert.Equal("https://github.com/acme/demo/commit/fedcba9876543210fedcba9876543210fedcba98",
                cut.Find(".short-id[title='fedcba9876543210fedcba9876543210fedcba98'] a.short-id__text").GetAttribute("href"));
        });
    }

    /// <summary>
    /// Recette R-373, runs without a recorded commit: an internal repository's run leads to its Aetheus
    /// commit and branch pages through the repository the backend resolved, never to the smart-HTTP
    /// clone URL (a host the browser cannot reach, a path that answers 404). When the repository was not
    /// resolved, the commit and the branch stay text.
    /// </summary>
    [Fact]
    public void AnInternalRepositoryRun_LeadsToItsAetheusPages_NeverToTheCloneUrl()
    {
        const string resolvedSha = "1111111111111111111111111111111111111111";
        const string unresolvedSha = "2222222222222222222222222222222222222222";
        var items = new List<PipelineRunDto>
        {
            new()
            {
                Id = 20, PipelineId = 1, PipelineName = "ci", Status = PipelineStatus.Success,
                BranchName = "feature/x", CommitHash = resolvedSha,
                RepositoryUrl = "https://host.docker.internal:5301/git/13/aetheus-self.git", RepositoryId = 42
            },
            new()
            {
                Id = 21, PipelineId = 1, PipelineName = "ci", Status = PipelineStatus.Success,
                BranchName = "develop", CommitHash = unresolvedSha,
                RepositoryUrl = "https://host.docker.internal:5301/git/13/aetheus-self"
            }
        };
        var cut = Render<PipelineRunsGrid>(parameters => parameters.Add(component => component.Items, items));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal($"/git-repositories/42/commits/{resolvedSha}",
                cut.Find($".short-id[title='{resolvedSha}'] a.short-id__text").GetAttribute("href"));
            Assert.Equal("/git-repositories/42?tab=branches&branch=feature%2Fx",
                cut.FindAll("a.pipeline-run-ref-link").Single(link => link.TextContent.Trim() == "feature/x").GetAttribute("href"));

            Assert.Empty(cut.FindAll($".short-id[title='{unresolvedSha}'] a"));
            Assert.Contains(cut.FindAll("span.pipeline-run-ref-link"), span => span.TextContent.Trim() == "develop");
            Assert.DoesNotContain(cut.FindAll("a[href]"), link =>
                link.GetAttribute("href")!.Contains("host.docker.internal", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void GlobalGrid_GroupsRunsByProject_AndRendersCurrentStep()
    {
        var items = new List<PipelineRunDto>
        {
            new()
            {
                Id = 10,
                PipelineId = 1,
                PipelineName = "ci",
                ProjectId = 1,
                ProjectName = "Alpha",
                Status = PipelineStatus.Running,
                Steps = [new PipelineStepRunDto { StepName = "Build", StageName = "Compile", Status = TaskExecutionStatus.Running }]
            },
            new()
            {
                Id = 11,
                PipelineId = 2,
                PipelineName = "qa",
                ProjectId = 2,
                ProjectName = "Beta",
                Status = PipelineStatus.Pending,
                Steps = [new PipelineStepRunDto { StepName = "Tests", StageName = "Verify", Status = TaskExecutionStatus.Running }]
            }
        };
        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.GroupByProject, true)
            .Add(component => component.Virtualize, true));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Alpha", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Beta", cut.Markup, StringComparison.Ordinal);
            // The status cell of a Running run shows its current step instead of a "Running" badge,
            // so "Build" appears while the badge does not.
            // Recette R-210: the Status header filter now lists every status by name, so the cell
            // assertions read the rows only.
            var rows = string.Concat(cut.FindAll("tbody tr[data-omni-row-index]").Select(row => row.OuterHtml));
            Assert.Contains("Build", rows, StringComparison.Ordinal);
            Assert.DoesNotContain("Enum_PipelineStatus_Running", rows, StringComparison.Ordinal);
            // A run in any other state keeps its status badge, and therefore does not surface the
            // step its status cell was replaced by.
            Assert.Contains("Enum_PipelineStatus_Pending", rows, StringComparison.Ordinal);
            Assert.DoesNotContain("Tests", rows, StringComparison.Ordinal);
            var dataGrid = cut.FindComponent<OmniDataGrid<PipelineRunTableItem>>().Instance;
            Assert.Equal(OmniDataGridScrollMode.Virtual, dataGrid.ScrollMode);
            Assert.False(dataGrid.AllowGrouping);
        });
    }

    [Fact]
    public void RowClick_NavigatesToProjectScopedRun()
    {
        var run = new PipelineRunDto { Id = 42, PipelineId = 7, PipelineName = "ci", ProjectId = 3 };
        var cut = Render<PipelineRunsGrid>(parameters => parameters.Add(component => component.Items, new List<PipelineRunDto> { run }));
        cut.Find("tr[data-omni-row-index='0']").Click();

        Assert.EndsWith("/pipelines/runs/42?projectId=3", Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Grid_RendersDurationAndServer()
    {
        var startedAt = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Local);
        var item = new PipelineRunDto
        {
            Id = 9,
            PipelineId = 4,
            PipelineName = "deploy",
            Status = PipelineStatus.Success,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(2).AddSeconds(5),
            Steps = [new PipelineStepRunDto { ServerId = 1, ServerName = "web-01" }]
        };

        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineRunDto> { item }));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Duration", cut.Markup);
            Assert.Contains("DurationMinutesSecondsFormat", cut.Markup);
            // The server used to be deliberately absent here. It is now a column of its own, so the
            // run table answers "where did this run" without opening the run.
            Assert.Contains("web-01", cut.Markup);
        });
    }

    [Fact]
    public void RunningDuration_AdvancesEverySecond_WithoutRefreshingTheGrid()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 12, 0, 5, TimeSpan.Zero));
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        localizer["DurationSecondsFormat"].Returns(new LocalizedString("DurationSecondsFormat", "{0}s"));
        Services.AddSingleton<TimeProvider>(time);
        Services.AddSingleton(localizer);
        var run = new PipelineRunDto
        {
            Id = 21,
            PipelineId = 4,
            PipelineName = "deploy",
            Status = PipelineStatus.Running,
            StartedAt = time.GetLocalNow().LocalDateTime.AddSeconds(-5)
        };

        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineRunDto> { run }));

        cut.WaitForAssertion(() => Assert.Equal("5s", cut.Find(".pipeline-run-duration").TextContent.Trim()));

        time.Advance(TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() => Assert.Equal("7s", cut.Find(".pipeline-run-duration").TextContent.Trim()));
    }

    [Fact]
    public void CompletedDuration_RemainsFixed_WhenTimeAdvances()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 12, 0, 5, TimeSpan.Zero));
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        localizer["DurationSecondsFormat"].Returns(new LocalizedString("DurationSecondsFormat", "{0}s"));
        Services.AddSingleton<TimeProvider>(time);
        Services.AddSingleton(localizer);
        var startedAt = time.GetLocalNow().LocalDateTime.AddSeconds(-10);
        var run = PipelineRunTableItem.FromRun(new PipelineRunDto
        {
            Id = 22,
            PipelineId = 4,
            PipelineName = "deploy",
            Status = PipelineStatus.Success,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(6)
        });

        var cut = Render<PipelineRunTableLiveDuration>(parameters => parameters
            .Add(component => component.Run, run));

        Assert.Equal("6s", cut.Markup.Trim());

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal("6s", cut.Markup.Trim());
    }

    [Fact]
    public void CompactGrid_ShowsRunIdFirst_AndDurationAfterStarted_WhenRequested()
    {
        var startedAt = new DateTime(2026, 8, 5, 10, 0, 0, DateTimeKind.Local);
        var run = new PipelineRunDto
        {
            Id = 15,
            PipelineId = 4,
            PipelineName = "deploy",
            Status = PipelineStatus.Success,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(3)
        };

        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineRunDto> { run })
            .Add(component => component.Compact, true)
            .Add(component => component.ShowDurationInCompact, true));

        cut.WaitForAssertion(() =>
        {
            // Column contract, in the order the run tables must keep: the id first - now carrying the
            // row actions inline instead of a column of its own - then the pipeline it belongs to and
            // its status right after. The former "Current step" column is gone: it is folded into the
            // status cell of a running run, so the wide live-updating value never costs a column.
            var columns = cut.FindComponents<OmniDataGridColumn<PipelineRunTableItem>>()
                .Select(column => column.Instance).ToList();
            Assert.Equal("RunId", columns[0].Property);
            Assert.Equal("pipeline-run-id-actions-column", columns[0].Class);
            Assert.Equal("PipelineName", columns[1].Property);
            Assert.Equal("Status", columns[2].Property);
            var titles = columns.Select(column => column.Title).ToList();
            Assert.Equal(
                ["ID", "Pipeline", "Status", "Project", "Started", "Completed", "Duration", "Branch", "Commit", "Server"],
                titles);
            Assert.Contains("DurationMinutesSecondsFormat", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MaxGroups_LimitsTheRenderedDataToTheTenMostRecentGroups()
    {
        var startedAt = new DateTime(2026, 8, 5, 10, 0, 0, DateTimeKind.Local);
        var runs = Enumerable.Range(1, 11)
            .Select(id => new PipelineRunDto
            {
                Id = id,
                PipelineId = id,
                PipelineName = $"pipeline-{id}",
                Status = PipelineStatus.Success,
                StartedAt = startedAt.AddMinutes(id)
            })
            .ToList();

        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, runs)
            .Add(component => component.MaxGroups, 10));

        cut.WaitForAssertion(() =>
        {
            var renderedRuns = cut.FindComponent<OmniDataGrid<PipelineRunTableItem>>().Instance.Items!.ToList();
            Assert.Equal(10, renderedRuns.Count);
            Assert.Equal(Enumerable.Range(2, 10).Reverse(), renderedRuns.Select(run => run.RunId));
        });
    }

    [Fact]
    public void FromRun_ProjectsTimingAndCurrentStep()
    {
        var startedAt = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Local);
        var run = new PipelineRunDto
        {
            Id = 9,
            PipelineId = 4,
            PipelineName = "deploy",
            ProjectId = 2,
            ProjectName = "Prod",
            Status = PipelineStatus.Running,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(42),
            Steps = [new PipelineStepRunDto { StepName = "Build", Status = TaskExecutionStatus.Running, ServerId = 8, ServerName = "web-01", ServerOs = "Linux" }]
        };

        var item = PipelineRunTableItem.FromRun(run);

        Assert.Equal(9, item.RunId);
        Assert.Equal("web-01", item.ServerName);
        Assert.Equal("Linux", item.ServerOs);
        Assert.Equal(startedAt, item.StartedAt);
        Assert.Equal(startedAt.AddSeconds(42), item.CompletedAt);
        Assert.Contains("Build", item.CurrentStep);
    }

    [Fact]
    public void LinkedRuns_AreGroupedUnderTheirRoot_AndAllGroupsStartCollapsed()
    {
        var oldStartedAt = new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Local);
        var latestStartedAt = oldStartedAt.AddHours(2);
        var runs = new List<PipelineRunDto>
        {
            new()
            {
                Id = 100,
                PipelineId = 1,
                PipelineName = "old-parent",
                Status = PipelineStatus.Success,
                StartedAt = oldStartedAt,
                Steps = [new PipelineStepRunDto { Id = 1, TriggeredRunId = 101 }]
            },
            new()
            {
                Id = 101,
                PipelineId = 2,
                PipelineName = "old-child",
                Status = PipelineStatus.Success,
                StartedAt = oldStartedAt.AddMinutes(1)
            },
            new()
            {
                Id = 200,
                PipelineId = 3,
                PipelineName = "latest-parent",
                Status = PipelineStatus.Running,
                StartedAt = latestStartedAt,
                Steps = [new PipelineStepRunDto { Id = 2, TriggeredRunId = 201 }]
            },
            new()
            {
                Id = 201,
                PipelineId = 4,
                PipelineName = "latest-child",
                Status = PipelineStatus.Running,
                StartedAt = latestStartedAt.AddMinutes(1)
            }
        };

        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, runs));

        cut.WaitForAssertion(() =>
        {
            var grid = cut.FindComponent<OmniDataGrid<PipelineRunTableItem>>().Instance;
            Assert.Equal(2, (grid.Items ?? []).Count());
            Assert.DoesNotContain("latest-child", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("old-child", cut.Markup, StringComparison.Ordinal);
            Assert.NotEmpty(cut.FindAll("button.omni-data-grid__expand"));
        });

        cut.Find("button.omni-data-grid__expand").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("latest-child", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("old-child", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ExpandedLinkedRuns_RemainOpen_WhenItemsRefresh()
    {
        // A running row with ShowDurationInCompact starts a real one-second timer on TimeProvider.System,
        // and its re-renders can starve WaitForAssertion under a loaded suite run - the check never gets
        // a turn, and the test fails with "Check count: 0" while the component rendered 160 times. A
        // controlled clock fires nothing, so what is asserted here is the expansion, not the machine.
        Services.AddSingleton<TimeProvider>(
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 5, 10, 5, 0, TimeSpan.Zero)));

        static List<PipelineRunDto> CreateRuns() =>
        [
            new PipelineRunDto
            {
                Id = 100,
                PipelineId = 1,
                PipelineName = "parent",
                Status = PipelineStatus.Running,
                StartedAt = new DateTime(2026, 8, 5, 10, 0, 0, DateTimeKind.Local),
                Steps = [new PipelineStepRunDto { Id = 1, TriggeredRunId = 101 }]
            },
            new PipelineRunDto
            {
                Id = 101,
                PipelineId = 2,
                PipelineName = "child",
                Status = PipelineStatus.Success,
                StartedAt = new DateTime(2026, 8, 5, 10, 1, 0, DateTimeKind.Local)
            }
        ];

        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, CreateRuns())
            .Add(component => component.Compact, true)
            .Add(component => component.ShowDurationInCompact, true));

        cut.Find("button.omni-data-grid__expand").Click();
        cut.WaitForAssertion(() => Assert.Contains("child", cut.Markup, StringComparison.Ordinal));

        cut.Render(parameters => parameters
            .Add(component => component.Items, CreateRuns())
            .Add(component => component.Compact, true)
            .Add(component => component.ShowDurationInCompact, true));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("child", cut.Markup, StringComparison.Ordinal);
            Assert.Single(cut.FindAll(".pipeline-linked-runs"));
            Assert.Contains("pipeline-run-tree-with-duration", cut.Find(".pipeline-run-tree").ClassList);
            Assert.Equal(
                [
                    "pipeline-run-tree-leaf",
                    "pipeline-run-tree-id",
                    "pipeline-name-link pipeline-run-tree-name",
                    "pipeline-run-tree-status",
                    "pipeline-run-tree-started",
                    "pipeline-run-tree-duration"
                ],
                cut.Find(".pipeline-run-tree-row").Children.Select(element => element.ClassName));
        });
    }

    [Fact]
    public void R2102_GroupRuns_PutsRunsInProgressFirst_MostRecentlyStartedFirst_ThenTheRestByDate()
    {
        // Recette R2-102: aetheus-deploy-prod #2543, still running, sat third under #2551 and #2544,
        // finished and started after it.
        var at = new DateTime(2026, 10, 4, 1, 0, 0);
        var runs = new List<PipelineRunDto>
        {
            new() { Id = 2551, PipelineId = 1, PipelineName = "aetheus-demo", Status = PipelineStatus.Failed, StartedAt = at.AddMinutes(30) },
            new() { Id = 2544, PipelineId = 2, PipelineName = "aetheus-nightly", Status = PipelineStatus.Success, StartedAt = at.AddMinutes(20) },
            new() { Id = 2543, PipelineId = 3, PipelineName = "aetheus-deploy-prod", Status = PipelineStatus.Running, StartedAt = at },
            new() { Id = 2540, PipelineId = 4, PipelineName = "release-fast", Status = PipelineStatus.WaitingForApproval, StartedAt = at.AddMinutes(5) },
            new() { Id = 2530, PipelineId = 5, PipelineName = "old", Status = PipelineStatus.Cancelled, StartedAt = at.AddMinutes(-60) }
        };

        Assert.Equal([2540, 2543, 2551, 2544, 2530], PipelineRunTableItem.GroupRuns(runs).Select(run => run.RunId));
    }

    [Fact]
    public void GroupRuns_PreservesTheDirectParentChildHierarchy()
    {
        var runs = new List<PipelineRunDto>
        {
            new()
            {
                Id = 10,
                PipelineId = 1,
                PipelineName = "parent",
                Steps = [new PipelineStepRunDto { Id = 1, TriggeredRunId = 11 }]
            },
            new()
            {
                Id = 11,
                PipelineId = 2,
                PipelineName = "child",
                Steps = [new PipelineStepRunDto { Id = 2, TriggeredRunId = 12 }]
            },
            new() { Id = 12, PipelineId = 3, PipelineName = "grandchild" }
        };

        var group = Assert.Single(PipelineRunTableItem.GroupRuns(runs));

        Assert.Equal(10, group.RunId);
        var child = Assert.Single(group.LinkedRuns);
        Assert.Equal(11, child.RunId);
        Assert.Equal(12, Assert.Single(child.LinkedRuns).RunId);
    }

    [Fact]
    public void NestedRuns_AppearOnlyAfterTheirDirectParentIsExpanded()
    {
        var runs = new List<PipelineRunDto>
        {
            new()
            {
                Id = 10,
                PipelineId = 1,
                PipelineName = "parent",
                StartedAt = new DateTime(2026, 7, 30, 12, 0, 0),
                Steps = [new PipelineStepRunDto { Id = 1, TriggeredRunId = 11 }]
            },
            new()
            {
                Id = 11,
                PipelineId = 2,
                PipelineName = "direct-child",
                StartedAt = new DateTime(2026, 7, 30, 12, 1, 0),
                Steps = [new PipelineStepRunDto { Id = 2, TriggeredRunId = 12 }]
            },
            new()
            {
                Id = 12,
                PipelineId = 3,
                PipelineName = "grandchild",
                StartedAt = new DateTime(2026, 7, 30, 12, 2, 0)
            }
        };

        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, runs));

        cut.WaitForAssertion(() => Assert.DoesNotContain("direct-child", cut.Markup, StringComparison.Ordinal));

        cut.Find("button.omni-data-grid__expand").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("direct-child", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("grandchild", cut.Markup, StringComparison.Ordinal);
        });

        cut.Find(".pipeline-run-tree-toggle").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("grandchild", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("CollapseLinkedPipelineRuns", cut.Markup, StringComparison.Ordinal);
        });
    }
}
