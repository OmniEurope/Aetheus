// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class PipelineRunTests : BunitContext
{
    public PipelineRunTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(
            HttpMethod.Get,
            "/runs?pageSize=50",
            []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(
            HttpMethod.Get,
            "api/ai/results",
            []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    [Fact]
    public void ReleaseEnrichmentFailure_DoesNotAbortRunPageInitialization()
    {
        _handler.SetJsonResponse("api/pipelines/runs/77", new PipelineRunDto
        {
            Id = 77,
            PipelineId = 5,
            PipelineName = "Release outage",
            Status = PipelineStatus.Success
        });
        _handler.SetResponse(HttpMethod.Get, "api/releases/by-run/77", System.Net.HttpStatusCode.ServiceUnavailable);

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 77));

        cut.WaitForState(() => typeof(PipelineRun).GetField("_live",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance) is not null);
        Assert.Contains("Release outage", cut.Markup);
    }

    [Fact]
    public void R2_045_AWaitingRun_ReadsItsApprovalBeforeTheRestOfThePage()
    {
        // The banner used to arrive after the first render and push the page down: its approval is
        // now read before the page shows, ahead of everything the page loads behind it.
        _handler.SetJsonResponse("api/pipelines/runs/78", new PipelineRunDto
        {
            Id = 78,
            PipelineId = 5,
            PipelineName = "Deliver",
            Status = PipelineStatus.WaitingForApproval
        });
        _handler.SetJsonResponse("api/pipelines/runs/78/approvals", new List<PipelineApprovalDto>
        {
            new() { Id = 31, PipelineRunId = 78, StageName = "Confirm", Scope = ApprovalScope.Pipeline, Status = ApprovalStatus.Pending }
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 78));

        cut.WaitForState(() => cut.Markup.Contains("PipelineApprovalTitle"), TimeSpan.FromSeconds(2));
        var urls = _handler.Requests.Select(request => request.Url).ToList();
        var approvals = urls.FindIndex(url => url.Contains("runs/78/approvals", StringComparison.Ordinal));
        var releases = urls.FindIndex(url => url.Contains("releases/by-run/78", StringComparison.Ordinal));
        Assert.InRange(approvals, 0, int.MaxValue);
        Assert.InRange(releases, approvals + 1, int.MaxValue);
    }

    [Fact]
    public void Renders_RunDetails()
    {
        _handler.SetJsonResponse("api/pipelines/runs/1", new PipelineRunDto
        {
            Id = 1,
            PipelineId = 5,
            PipelineName = "CI Build",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1,
                    StepName = "Build",
                    StageName = "build",
                    Status = TaskExecutionStatus.Success,
                    StartedAt = DateTime.UtcNow.AddMinutes(-5),
                    CompletedAt = DateTime.UtcNow
                }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 1));
        cut.WaitForState(() => cut.Markup.Contains("CI Build"), TimeSpan.FromSeconds(2));

        Assert.Contains("CI Build", cut.Markup);
        Assert.Contains("Build", cut.Markup);
    }

    [Fact]
    public void ProjectRun_Breadcrumb_Includes_Project_Pipeline_And_Run()
    {
        _handler.SetJsonResponse("api/pipelines/runs/80", new PipelineRunDto
        {
            Id = 80,
            PipelineId = 5,
            ProjectId = 2,
            ProjectName = "Toto",
            PipelineName = "CI Build",
            Status = PipelineStatus.Success
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 80));
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();

        cut.WaitForAssertion(() => Assert.Equal(
            ["Projects", "Toto", "Pipelines", "CI Build", "PipelineRun #80"],
            breadcrumb.Items.Select(item => item.Text)));
        Assert.Equal("/pipelines/5?projectId=2", breadcrumb.Items[3].Href);
    }

    [Fact]
    public void GateLoadFailure_RendersVisibleIncompleteWarning()
    {
        _handler.SetJsonResponse("api/pipelines/runs/702", new PipelineRunDto
        {
            Id = 702,
            PipelineId = 5,
            PipelineName = "quality",
            Status = PipelineStatus.Success
        });
        _handler.SetResponse(
            HttpMethod.Get,
            "api/analysis/runs/702/result",
            System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 702));

        cut.WaitForAssertion(() => Assert.Contains("AnalysisGateIncompleteTitle", cut.Markup));
        Assert.Contains("702", cut.Markup);
    }

    [Fact]
    public void GateResult_RendersCompactOverviewTileAndSingleGateTabAfterLogs()
    {
        _handler.SetJsonResponse("api/pipelines/runs/701", new PipelineRunDto
        {
            Id = 701,
            PipelineId = 5,
            PipelineName = "quality",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/701/result", new AnalysisRunGateDto
        {
            PipelineRunId = 701,
            Status = AnalysisGateStatus.Warning,
            ReportCount = 1,
            FindingCount = 1,
            NewFindingCount = 1,
            Findings =
            [
                new AnalysisRunGateFindingDto
                {
                    FindingId = 17,
                    Title = "Unsafe command",
                    Message = "Untrusted input reaches a command sink.",
                    RuleId = "security.command",
                    Category = AnalysisCategory.Sast,
                    Severity = AnalysisSeverity.High,
                    Status = AnalysisFindingStatus.Open,
                    FilePath = "src/Runner.cs",
                    StartLine = 42,
                    IsNew = true
                }
            ],
            Reports = [new AnalysisRunGateReportDto { ReportId = 9, FindingCount = 1 }]
        });
        // Recette R-485: the Gate tab reads its figures and its rows from the run findings route.
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings", new AnalysisRunFindingsPageDto
        {
            Items =
            [
                new AnalysisRunGateFindingDto
                {
                    FindingId = 17, Title = "Unsafe command", Message = "Untrusted input reaches a command sink.",
                    RuleId = "security.command", Category = AnalysisCategory.Sast, Severity = AnalysisSeverity.High,
                    Status = AnalysisFindingStatus.Open, FilePath = "src/Runner.cs", StartLine = 42, IsNew = true
                }
            ],
            TotalCount = 1,
            Page = 1,
            PageSize = 25,
            OpenCount = 1,
            NewOpenCount = 1
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 701));

        cut.WaitForState(() => cut.Markup.Contains("AnalysisGateOverview", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        var overview = cut.Markup.IndexOf("Overview", StringComparison.Ordinal);
        var logs = cut.Markup.IndexOf("Logs", overview, StringComparison.Ordinal);
        var gate = cut.Markup.IndexOf("AnalysisGateTab", logs, StringComparison.Ordinal);
        Assert.True(overview >= 0 && overview < logs && logs < gate);
        Assert.DoesNotContain("AnalysisFindingsReport", cut.Find(".omni-tabs__viewport").TextContent);
        var gateTile = cut.Find(".run-overview-tiles .analysis-run-gate-tile");
        Assert.Contains("AnalysisGateOverview", gateTile.TextContent);
        Assert.Contains("AnalysisGateFindingsCount", gateTile.TextContent);
        gateTile.Click();
        // PLAN-003 lot 21 removed the description line under the Gate heading; the heading itself
        // and the finding rows prove the tile opened the Gate tab.
        cut.WaitForState(() => cut.Markup.Contains("analysis-run-gate-page", StringComparison.Ordinal));
        Assert.Contains("/analysis/findings/17", cut.Markup);
        // Recette R2-053: title and location on one line each, path:line, the message on hover.
        Assert.EndsWith("Untrusted input reaches a command sink.", cut.Find(".analysis-finding-grid-title").GetAttribute("title"), StringComparison.Ordinal);
        Assert.Equal("src/Runner.cs:42", cut.Find(".analysis-finding-location").TextContent);
    }

    [Fact]
    public void RunningRun_DoesNotLoadOrRenderGate()
    {
        _handler.SetJsonResponse("api/pipelines/runs/703", new PipelineRunDto
        {
            Id = 703,
            PipelineId = 5,
            PipelineName = "running-quality",
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/703/result", new AnalysisRunGateDto
        {
            PipelineRunId = 703,
            Status = AnalysisGateStatus.Warning,
            Findings = [new AnalysisRunGateFindingDto { FindingId = 99, Title = "Premature" }]
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 703));

        cut.WaitForState(() => cut.Markup.Contains("running-quality", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".analysis-run-gate-tile"));
        Assert.DoesNotContain("AnalysisGateTab", cut.Find(".omni-tabs__viewport").TextContent);
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("api/analysis/runs/703/result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunParameters_RenderAsOneTileAndOpenDialog()
    {
        _handler.SetJsonResponse("api/pipelines/runs/704", new PipelineRunDto
        {
            Id = 704,
            PipelineId = 5,
            PipelineName = "parameterized",
            Status = PipelineStatus.Success,
            Parameters = new Dictionary<string, string>
            {
                ["environment"] = "staging",
                ["replicas"] = "3"
            }
        });
        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 704));
        cut.WaitForState(() => cut.FindAll(".run-parameters-tile").Count == 1);

        Assert.DoesNotContain("run-params-grid", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("RunParametersCount", cut.Find(".run-parameters-tile").TextContent);

        var dialog = Services.GetRequiredService<OmniDialogService>();
        var opened = false;
        dialog.OnOpen += (_, _, _, _) => opened = true;
        var method = typeof(PipelineRun).GetMethod("ShowParameters", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(null));
        await task;
        Assert.True(opened);
    }

    [Fact]
    public void RunParametersDialog_RendersLocalizedKeyValueGrid()
    {
        var cut = Render<PipelineRunParametersDialog>(parameters => parameters.Add(component => component.Parameters,
            new Dictionary<string, string> { ["replicas"] = "3", ["environment"] = "staging" }));

        Assert.Contains("Parameter", cut.Markup);
        Assert.Contains("Value", cut.Markup);
        Assert.Contains("environment", cut.Markup);
        Assert.Contains("staging", cut.Markup);
    }

    [Fact]
    public void ParentRun_GateTileUsesAggregatedChildGate()
    {
        _handler.SetJsonResponse("api/pipelines/runs/710", new PipelineRunDto
        {
            Id = 710,
            PipelineId = 5,
            PipelineName = "parent",
            Status = PipelineStatus.Success,
            Steps = [new PipelineStepRunDto { Id = 1, Status = TaskExecutionStatus.Success, TriggeredRunId = 711 }]
        });
        _handler.SetJsonResponse("api/pipelines/runs/711", new PipelineRunDto
        {
            Id = 711,
            PipelineId = 6,
            PipelineName = "quality-child",
            Status = PipelineStatus.Success
        });
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/710/result", System.Net.HttpStatusCode.NoContent);
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/711/result", new AnalysisRunGateDto
        {
            PipelineRunId = 711,
            Status = AnalysisGateStatus.Warning,
            FindingCount = 4,
            Findings = [new AnalysisRunGateFindingDto { FindingId = 71, Title = "Child finding" }]
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 710));

        cut.WaitForAssertion(() =>
        {
            var tile = cut.Find(".analysis-run-gate-tile");
            Assert.Contains("AnalysisGateStatusWarning", tile.TextContent);
            Assert.Contains("AnalysisGateFindingsCount", tile.TextContent);
        }, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Artifacts_RenderOneCountTileAndOneListTab()
    {
        _handler.SetJsonResponse("api/pipelines/runs/712", new PipelineRunDto
        {
            Id = 712,
            PipelineId = 5,
            PipelineName = "artifacts",
            Status = PipelineStatus.Success,
            Artifacts =
            [
                new PipelineArtifactDto { Id = 1, Name = "app.zip", SizeBytes = 1024, StageName = "package" },
                new PipelineArtifactDto { Id = 2, Name = "coverage.xml", SizeBytes = 2048, StageName = "quality" }
            ]
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 712));
        cut.WaitForState(() => cut.FindAll(".run-artifacts-tile").Count == 1, TimeSpan.FromSeconds(2));

        var tile = Assert.Single(cut.FindAll(".run-artifacts-tile"));
        Assert.Contains("Artifacts", tile.TextContent);
        Assert.Contains("2", tile.TextContent);
        tile.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("app.zip", cut.Markup);
            Assert.Contains("coverage.xml", cut.Markup);
        });
    }

    [Fact]
    public void Artifacts_ShowTheirSizeAndDigest()
    {
        // PLAN-006 lot 12.5. The store computes a digest over the exact bytes it kept and recorded
        // it; nothing showed it, so an artifact could not be identified across a restore.
        _handler.SetJsonResponse("api/pipelines/runs/713", new PipelineRunDto
        {
            Id = 713,
            PipelineId = 5,
            PipelineName = "artifacts",
            Status = PipelineStatus.Success,
            Artifacts =
            [
                new PipelineArtifactDto
                {
                    Id = 1, Name = "app.zip", SizeBytes = 1536, StageName = "package",
                    Sha256 = "0123456789abcdef" + new string('0', 48)
                },
                // Published before the store computed one: an empty cell would read as "not verified".
                new PipelineArtifactDto { Id = 2, Name = "legacy.zip", SizeBytes = 10, StageName = "package" }
            ]
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 713));
        cut.WaitForState(() => cut.FindAll(".run-artifacts-tile").Count == 1, TimeSpan.FromSeconds(2));
        Assert.Single(cut.FindAll(".run-artifacts-tile")).Click();

        cut.WaitForAssertion(() => Assert.Contains("app.zip", cut.Markup));
        // Shortened on screen, complete in the title, so it can be copied and compared.
        Assert.Contains("0123456789ab", cut.Markup);
        Assert.Contains("0123456789abcdef" + new string('0', 48), cut.Markup);
        // Recette R-373: the short digest is the link to its artifact.
        var digest = cut.Find(".run-artifact-digest a.short-id__text");
        Assert.Equal("/artifacts/1", digest.GetAttribute("href"));
        Assert.Equal("01234567", digest.TextContent);
        Assert.Contains(PipelineRunFormatting.FormatSize(1536), cut.Markup);
        Assert.Contains("DigestUnavailable", cut.Markup);
    }

    [Fact]
    public void ChildRun_RendersLinkedParentPipelineTile()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos?projectId=8", new List<GitLightRepoDto>());
        _handler.SetJsonResponse("api/pipelines/runs/7/lineage", new PipelineRunLineageDto
        {
            Downstream = [new PipelineRunLinkDto { RunId = 51, PipelineId = 9, ProjectId = 8, PipelineName = "deploy-prod", BuildNumber = 12, Status = PipelineStatus.WaitingForApproval }]
        });
        _handler.SetJsonResponse("api/pipelines/runs/7", new PipelineRunDto
        {
            Id = 7,
            PipelineId = 5,
            ProjectId = 8,
            PipelineName = "child-ci",
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow,
            ResolvedVariables = new Dictionary<string, string>
            {
                ["UPSTREAM_PIPELINE"] = "release-parent",
                ["UPSTREAM_RUN_ID"] = "42",
                ["UPSTREAM_CHAIN"] = "8"
            }
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 7));
        // R-498: the lineage tile names the parent run and the run this one started, both as links.
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("a.run-lineage-child")), TimeSpan.FromSeconds(2));

        var tile = cut.Find(".run-lineage-tile");
        Assert.Contains("RunLineage", tile.TextContent);
        var parent = tile.QuerySelector("a.run-lineage-parent")!;
        Assert.Equal("/pipelines/runs/42?projectId=8", parent.GetAttribute("href"));
        Assert.Contains("release-parent #42", parent.TextContent);
        var child = tile.QuerySelector("a.run-lineage-child")!;
        Assert.Equal("/pipelines/runs/51?projectId=8", child.GetAttribute("href"));
        Assert.Contains("deploy-prod #12", child.TextContent);
        // It sits right after the source tile.
        var tiles = cut.FindAll(".run-overview-tiles > *").ToList();
        var lineageAt = tiles.FindIndex(element => element.ClassList.Contains("run-lineage-tile"));
        Assert.True(lineageAt > 0 && tiles[lineageAt - 1].ClassList.Contains("run-source-tile") || tiles.All(element => !element.ClassList.Contains("run-source-tile")));
    }

    [Fact]
    public void R498_ARunSomeoneLaunched_NamesThatPerson_AndAnAutomaticOneSaysSo()
    {
        var run = new PipelineRunDto { Id = 7, PipelineId = 5, PipelineName = "candidate", Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };
        _handler.SetJsonResponse("api/pipelines/runs/7/lineage", new PipelineRunLineageDto { TriggeredBy = "sony" });

        var launched = Render<PipelineRunLineageTile>(parameters => parameters.Add(component => component.Run, run));

        launched.WaitForAssertion(() => Assert.Equal("sony", launched.Find(".run-lineage-actor").TextContent));
        Assert.Contains("RunLaunchedBy", launched.Markup);
        Assert.Empty(launched.FindAll(".run-lineage-child"));

        _handler.SetJsonResponse("api/pipelines/runs/8/lineage", new PipelineRunLineageDto());
        var automatic = Render<PipelineRunLineageTile>(parameters => parameters.Add(component => component.Run, run with { Id = 8 }));

        automatic.WaitForAssertion(() => Assert.Equal("RunLaunchedAutomatically", automatic.Find(".run-lineage-actor").TextContent));
    }

    [Fact]
    public void RunIdParameterChange_ReloadsDisplayedRun()
    {
        _handler.SetJsonResponse("api/pipelines/runs/317", new PipelineRunDto
        {
            Id = 317,
            PipelineId = 10,
            PipelineName = "parent-release",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        _handler.SetJsonResponse("api/pipelines/runs/318", new PipelineRunDto
        {
            Id = 318,
            PipelineId = 11,
            PipelineName = "child-ci",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 317));
        cut.WaitForState(() => cut.Markup.Contains("parent-release"), TimeSpan.FromSeconds(2));

        cut.Render(parameters => parameters.Add(component => component.RunId, 318));
        cut.WaitForState(() => cut.Markup.Contains("child-ci"), TimeSpan.FromSeconds(2));

        // The header title is the run identity: it has to follow the parameter change, not just the
        // body. "child-ci" appears in the run card before the header re-renders, so wait on the title.
        cut.WaitForAssertion(
            () => Assert.Contains("PipelineRun #318", cut.Find(".omni-page-header__title").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
        Assert.Contains("child-ci", cut.Markup);
        Assert.DoesNotContain("parent-release", cut.Markup);
    }

    [Fact]
    public async Task ReloadTerminalRun_StopsIsolatedDurationTimers()
    {
        _handler.SetJsonResponse("api/pipelines/runs/51", new PipelineRunDto
        {
            Id = 51,
            PipelineId = 5,
            PipelineName = "live",
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 51));
        cut.WaitForState(() => cut.FindComponents<PipelineRunLiveDuration>().Any(component =>
            typeof(Aetheus.Front.Components.Pipelines.LiveDurationComponentBase).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(component.Instance) is not null));
        _handler.SetJsonResponse("api/pipelines/runs/51", new PipelineRunDto
        {
            Id = 51,
            PipelineId = 5,
            PipelineName = "live",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow
        });
        var reload = typeof(PipelineRun).GetMethod("ReloadRunAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(async () => await (Task)reload.Invoke(cut.Instance, [])!);

        Assert.All(cut.FindComponents<PipelineRunLiveDuration>(), component =>
            Assert.Null(typeof(Aetheus.Front.Components.Pipelines.LiveDurationComponentBase).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(component.Instance)));
    }

    [Fact]
    public void RepositoryTile_MatchesRunUrlInsteadOfTakingFirstProjectRepository()
    {
        _handler.SetJsonResponse("api/pipelines/runs/52", new PipelineRunDto
        {
            Id = 52,
            PipelineId = 6,
            ProjectId = 8,
            PipelineName = "build",
            ProjectName = "Project",
            RepositoryUrl = "https://git.example/acme/right.git",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow
        });
        _handler.SetResponse("api/pipelines/6/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 11, ProjectId = 8, Name = "wrong", CloneUrl = "https://git.example/acme/wrong.git" },
            new() { Id = 22, ProjectId = 8, Name = "right", CloneUrl = "https://git.example/acme/right" }
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 52));

        cut.WaitForAssertion(() => Assert.Contains("/git-repositories/22", cut.Markup));
        Assert.DoesNotContain("/git-repositories/11", cut.Markup);
        Assert.Equal(22, typeof(PipelineRun).GetField("_repoId",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance));
    }

    [Fact]
    public void Renders_LoadingState()
    {
        _handler.SetJsonResponse<PipelineRunDto?>("api/pipelines/runs/99", null);

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 99));
        // Null run data -> the page renders its "run not found" empty state, not the run details.
        cut.WaitForState(() => cut.Markup.Contains("RunNotFound"), TimeSpan.FromSeconds(2));
        Assert.Contains("RunNotFound", cut.Markup);
        Assert.DoesNotContain("pipeline-run-toolbar", cut.Markup);
    }

    [Fact]
    public void Groups_StepsByStage()
    {
        _handler.SetJsonResponse("api/pipelines/runs/2", new PipelineRunDto
        {
            Id = 2,
            PipelineName = "Deploy",
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1, StepName = "Checkout", StageName = "prepare",
                    Status = TaskExecutionStatus.Success,
                    StartedAt = DateTime.UtcNow.AddMinutes(-3), CompletedAt = DateTime.UtcNow.AddMinutes(-2)
                },
                new PipelineStepRunDto
                {
                    Id = 2, StepName = "Build", StageName = "build",
                    Status = TaskExecutionStatus.Running,
                    StartedAt = DateTime.UtcNow.AddMinutes(-1)
                },
                new PipelineStepRunDto
                {
                    Id = 3, StepName = "Test", StageName = "build",
                    Status = TaskExecutionStatus.Pending
                }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 2));
        cut.WaitForState(() => cut.Markup.Contains("Deploy"), TimeSpan.FromSeconds(2));

        // Verify the real grouping contract on the computed stage model: the 3 steps collapse into 2
        // stages - "prepare" (1 step) and "build" (2 steps). (The timeline markup lives behind a
        // tab; asserting _stages is a deterministic check of the grouping logic itself.)
        var byName = StageStepCounts(cut);
        Assert.Equal(2, byName.Count);
        Assert.Equal(1, byName["prepare"]);
        Assert.Equal(2, byName["build"]);
    }

    // Reflects over the component's computed _stages (List of internal StageViewModel) to expose each
    // stage's Name and step count - the grouping contract without depending on tab/markup rendering.
    private static Dictionary<string, int> StageStepCounts(IRenderedComponent<PipelineRun> cut)
    {
        var stages = (System.Collections.IEnumerable)typeof(PipelineRun)
            .GetField("_stages", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        return stages.Cast<object>().ToDictionary(
            s => (string)s.GetType().GetProperty("Name")!.GetValue(s)!,
            s => ((System.Collections.ICollection)s.GetType().GetProperty("Steps")!.GetValue(s)!).Count);
    }

    [Fact]
    public void MultiStage_ShowsDurations()
    {
        _handler.SetJsonResponse("api/pipelines/runs/3", new PipelineRunDto
        {
            Id = 3,
            PipelineName = "Full",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1, StepName = "A", StageName = "s1",
                    Status = TaskExecutionStatus.Success,
                    StartedAt = DateTime.UtcNow.AddMinutes(-10),
                    CompletedAt = DateTime.UtcNow.AddMinutes(-5)
                },
                new PipelineStepRunDto
                {
                    Id = 2, StepName = "B", StageName = "s2",
                    Status = TaskExecutionStatus.Success,
                    StartedAt = DateTime.UtcNow.AddMinutes(-5),
                    CompletedAt = DateTime.UtcNow
                }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 3));
        cut.WaitForState(() => cut.Markup.Contains("Full"), TimeSpan.FromSeconds(2));

        // Each stage step spans 5 minutes, so a rendered "5m ..s" duration must appear in the timeline.
        Assert.Contains("run-tl-dur", cut.Markup);
        Assert.Matches(@"\d+m \d+s", cut.Markup);
    }

    // --- Static method tests ---

    [Fact]
    public void FormatDuration_NullStart_ReturnsEmpty()
    {
        var result = PipelineRunFormatting.FormatDuration(null, null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatDuration_WithValues_ReturnsFormatted()
    {
        var started = DateTime.UtcNow.AddMinutes(-2).AddSeconds(-30);
        var completed = DateTime.UtcNow;
        var result = PipelineRunFormatting.FormatDuration(started, completed);
        Assert.Contains("m", result);
    }

    [Fact]
    public void FormatDuration_ShortDuration_ShowsSeconds()
    {
        var started = DateTime.UtcNow;
        var completed = started.AddSeconds(45);
        var result = PipelineRunFormatting.FormatDuration(started, completed);
        Assert.Equal("45s", result);
    }

    [Fact]
    public void FormatDuration_FutureStartNeverShowsNegativeTime()
    {
        var result = PipelineRunFormatting.FormatDuration(DateTime.UtcNow.AddMinutes(1), null);

        Assert.Equal("0s", result);
    }

    [Fact]
    public void StepProgress_TotalCountsStepsTheRunNeverReached()
    {
        var run = new PipelineRunDto
        {
            Status = PipelineStatus.Success,
            Steps = Enumerable.Range(1, 14)
                .Select(id => new PipelineStepRunDto { Id = id, Status = TaskExecutionStatus.Success })
                .Concat([
                    new PipelineStepRunDto { Id = 15, Status = TaskExecutionStatus.Pending },
                    new PipelineStepRunDto { Id = 16, Status = TaskExecutionStatus.Pending }
                ])
                .ToList()
        };

        Assert.Equal((14, 16), PipelineRunPresentation.StepProgress(run));
        Assert.Equal((14, 16), PipelineRunPresentation.StepProgress(run with { Status = PipelineStatus.Cancelled }));
        Assert.Equal((14, 16), PipelineRunPresentation.StepProgress(run with { Status = PipelineStatus.Running }));
    }

    [Theory]
    [InlineData(PipelineStatus.Success, OmniTone.Success)]
    [InlineData(PipelineStatus.Failed, OmniTone.Danger)]
    [InlineData(PipelineStatus.Running, OmniTone.Accent)]
    public void GetRunBadge_ReturnsExpectedStyle(PipelineStatus status, OmniTone expected)
    {
        var result = PipelineRunFormatting.GetRunBadge(status);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Success, OmniTone.Success)]
    [InlineData(TaskExecutionStatus.Failed, OmniTone.Danger)]
    [InlineData(TaskExecutionStatus.Running, OmniTone.Accent)]
    [InlineData(TaskExecutionStatus.Pending, OmniTone.Neutral)]
    public void GetStepBadge_ReturnsExpectedStyle(TaskExecutionStatus status, OmniTone expected)
    {
        var result = PipelineRunFormatting.GetStepBadge(status);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MatrixGroupKey_ReturnsCorrectFormat()
    {
        var result = PipelineRunFormatting.MatrixGroupKey("build", "compile");
        Assert.Equal("build::compile", result);
    }

    // --- SelectStep ---

    [Fact]
    public void SelectStep_SetsSelectedStep()
    {
        _handler.SetJsonResponse("api/pipelines/runs/4", new PipelineRunDto
        {
            Id = 4,
            PipelineName = "Toggle Test",
            Status = PipelineStatus.Success,
            Steps =
            [
                new PipelineStepRunDto { Id = 1, StepName = "A", StageName = "s1", Status = TaskExecutionStatus.Success }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 4));
        cut.WaitForState(() => cut.Markup.Contains("Toggle Test"), TimeSpan.FromSeconds(2));

        var selected = typeof(PipelineRun).GetField("_selectedStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Auto-selects the last completed step
        Assert.NotNull(selected.GetValue(cut.Instance));
    }

    [Fact]
    public async Task ReloadRunAsync_KeepsManualSelectionPinnedWhileAnotherStepRuns()
    {
        var manuallySelected = new PipelineStepRunDto
        {
            Id = 101,
            StepName = "Inspect",
            StageName = "build",
            Status = TaskExecutionStatus.Success
        };
        _handler.SetJsonResponse("api/pipelines/runs/401", new PipelineRunDto
        {
            Id = 401,
            PipelineName = "Pinned selection",
            Status = PipelineStatus.Running,
            Steps =
            [
                manuallySelected,
                new PipelineStepRunDto
                {
                    Id = 102,
                    StepName = "Deploy",
                    StageName = "deploy",
                    Status = TaskExecutionStatus.Running
                }
            ]
        });
        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 401));
        cut.WaitForState(() => cut.Markup.Contains("Pinned selection"), TimeSpan.FromSeconds(2));
        await cut.InvokeAsync(() => cut.Instance.SelectStep(manuallySelected));

        _handler.SetJsonResponse("api/pipelines/runs/401", new PipelineRunDto
        {
            Id = 401,
            PipelineName = "Pinned selection",
            Status = PipelineStatus.Running,
            Steps =
            [
                manuallySelected with { CompletedAt = DateTime.UtcNow },
                new PipelineStepRunDto
                {
                    Id = 102,
                    StepName = "Deploy",
                    StageName = "deploy",
                    Status = TaskExecutionStatus.Running
                }
            ]
        });
        await cut.InvokeAsync(cut.Instance.ReloadRunAsync);

        var selected = Assert.IsType<PipelineStepRunDto>(cut.Instance.SelectedStep);
        Assert.Equal(101, selected.Id);
        Assert.True(cut.Instance.IsStepSelectionPinned);
    }

    // --- ToggleMatrixGroup ---

    [Fact]
    public void ToggleMatrixGroup_AddsAndRemoves()
    {
        _handler.SetJsonResponse("api/pipelines/runs/5", new PipelineRunDto
        {
            Id = 5,
            PipelineName = "Matrix",
            Status = PipelineStatus.Success,
            Steps = [new PipelineStepRunDto { Id = 1, StepName = "A", StageName = "s1", Status = TaskExecutionStatus.Success }]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 5));
        cut.WaitForState(() => cut.Markup.Contains("Matrix"), TimeSpan.FromSeconds(2));

        var method = typeof(PipelineRun).GetMethod("ToggleMatrixGroup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, ["s1::A"]);

        var groups = (HashSet<string>)typeof(PipelineRun).GetField("_expandedMatrixGroups", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Contains("s1::A", groups);

        method.Invoke(cut.Instance, ["s1::A"]);
        Assert.DoesNotContain("s1::A", groups);
    }

    // --- GetAggregateStatus ---

    [Fact]
    public void GetAggregateStatus_AllSuccess_ReturnsSuccess()
    {
        var steps = new List<PipelineStepRunDto>
        {
            new() { Status = TaskExecutionStatus.Success },
            new() { Status = TaskExecutionStatus.Success }
        };
        var result = PipelineRunFormatting.GetAggregateStatus(steps);
        Assert.Equal(TaskExecutionStatus.Success, result);
    }

    [Fact]
    public void GetAggregateStatus_OneFailed_ReturnsFailed()
    {
        var steps = new List<PipelineStepRunDto>
        {
            new() { Status = TaskExecutionStatus.Success },
            new() { Status = TaskExecutionStatus.Failed }
        };
        var result = PipelineRunFormatting.GetAggregateStatus(steps);
        Assert.Equal(TaskExecutionStatus.Failed, result);
    }

    [Fact]
    public void GetAggregateStatus_OneRunning_ReturnsRunning()
    {
        var steps = new List<PipelineStepRunDto>
        {
            new() { Status = TaskExecutionStatus.Success },
            new() { Status = TaskExecutionStatus.Running }
        };
        var result = PipelineRunFormatting.GetAggregateStatus(steps);
        Assert.Equal(TaskExecutionStatus.Running, result);
    }

    [Fact]
    public void GetAggregateStatus_AllPending_ReturnsPending()
    {
        var steps = new List<PipelineStepRunDto>
        {
            new() { Status = TaskExecutionStatus.Pending },
            new() { Status = TaskExecutionStatus.Pending }
        };
        var result = PipelineRunFormatting.GetAggregateStatus(steps);
        Assert.Equal(TaskExecutionStatus.Pending, result);
    }

    // --- Failed run rendering ---

    [Fact]
    public void Renders_FailedRun()
    {
        _handler.SetJsonResponse("api/pipelines/runs/6", new PipelineRunDto
        {
            Id = 6,
            PipelineName = "Failed Pipeline",
            Status = PipelineStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow,
            Steps =
            [
                new PipelineStepRunDto { Id = 1, StepName = "Build", StageName = "build", Status = TaskExecutionStatus.Success },
                new PipelineStepRunDto { Id = 2, StepName = "Deploy", StageName = "deploy", Status = TaskExecutionStatus.Failed }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 6));
        cut.WaitForState(() => cut.Markup.Contains("Failed Pipeline"), TimeSpan.FromSeconds(2));
        Assert.Contains("Failed Pipeline", cut.Markup);
    }

    [Fact]
    public void SelectStep_AutoSelectsLastCompleted()
    {
        _handler.SetJsonResponse("api/pipelines/runs/10", new PipelineRunDto
        {
            Id = 10,
            PipelineName = "Toggle Test",
            Status = PipelineStatus.Success,
            Steps =
            [
                new PipelineStepRunDto { Id = 1, StepName = "s1", StageName = "build", Status = TaskExecutionStatus.Success },
                new PipelineStepRunDto { Id = 2, StepName = "s2", StageName = "deploy", Status = TaskExecutionStatus.Success }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 10));
        cut.WaitForState(() => cut.Markup.Contains("Toggle Test"), TimeSpan.FromSeconds(2));

        var selected = typeof(PipelineRun).GetField("_selectedStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var step = selected.GetValue(cut.Instance) as PipelineStepRunDto;
        // Should auto-select the last completed step (s2)
        Assert.NotNull(step);
        Assert.Equal("s2", step!.StepName);
    }

    [Fact]
    public void MatrixGroupKey_CombinesStageName()
    {
        var result = PipelineRunFormatting.MatrixGroupKey("build", "test-step");
        Assert.Equal("build::test-step", result);
    }

    [Fact]
    public void Renders_RunWithMultipleStages()
    {
        _handler.SetJsonResponse("api/pipelines/runs/20", new PipelineRunDto
        {
            Id = 20,
            PipelineName = "Multi Stage",
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            Steps =
            [
                new PipelineStepRunDto { Id = 1, StepName = "Compile", StageName = "build", Status = TaskExecutionStatus.Success, StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow.AddMinutes(-3) },
                new PipelineStepRunDto { Id = 2, StepName = "UnitTest", StageName = "test", Status = TaskExecutionStatus.Running, StartedAt = DateTime.UtcNow.AddMinutes(-2) },
                new PipelineStepRunDto { Id = 3, StepName = "Deploy", StageName = "deploy", Status = TaskExecutionStatus.Pending }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 20));
        cut.WaitForState(() => cut.Markup.Contains("Multi Stage"), TimeSpan.FromSeconds(2));
        Assert.Contains("Multi Stage", cut.Markup);
        // All three stages (build/test/deploy) must be grouped from the steps.
        var byName = StageStepCounts(cut);
        Assert.Contains("build", byName.Keys);
        Assert.Contains("test", byName.Keys);
        Assert.Contains("deploy", byName.Keys);
    }
}
