// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// The banner itself, rendered: a unit test over the summary proves the list, not that the page puts
/// it on screen. These render the real page and read its markup.
/// </summary>
public class PipelineRunWaitingBannerTests : BunitContext
{
    public PipelineRunWaitingBannerTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    private const string Reason = "Stage 'Build images' waits for a configured pipeline runner to come back online.";

    private void ServeRun(int runId, PipelineStatus status, string? waitingReason)
    {
        _handler.SetJsonResponse($"api/pipelines/runs/{runId}", new PipelineRunDto
        {
            Id = runId,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = status,
            WaitingReason = waitingReason,
            WaitingSince = DateTime.UtcNow.AddMinutes(-3)
        });
    }

    [Fact]
    public void ALiveRunWithAWaitingReason_ShowsItOnThePage()
    {
        ServeRun(81, PipelineStatus.Running, Reason);

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 81));

        cut.WaitForAssertion(() => Assert.Contains("Build images", cut.Markup));
        Assert.Contains("come back online", cut.Markup);
    }

    [Fact]
    public void AFinishedRun_ShowsNoWait()
    {
        // The column survives the run for post-mortem reading, but a finished run is not waiting for
        // anything, so the page must not claim it is.
        ServeRun(82, PipelineStatus.Success, Reason);

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 82));

        cut.WaitForAssertion(() => Assert.Contains("aetheus-candidate", cut.Markup));
        Assert.DoesNotContain("come back online", cut.Markup);
    }

    [Fact]
    public void ThePreflightRecord_ListsWhatWasVerifiedAtLaunch()
    {
        _handler.SetJsonResponse("api/pipelines/runs/83", new PipelineRunDto
        {
            Id = 83,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            PreflightChecks =
            [
                new PreflightCheckDto { Kind = "environment", Subject = "qa" },
                new PreflightCheckDto { Kind = "child-pipeline", Subject = "aetheus-ci" }
            ]
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 83));

        cut.WaitForAssertion(() => Assert.Contains("run-preflight-check", cut.Markup));
        Assert.Contains("aetheus-ci", cut.Markup);
        Assert.Contains("qa", cut.Markup);
        // R1-33: an OE collapsible, folded by default (a native details without the open attribute).
        var record = cut.Find("[data-testid='preflight-record']");
        var details = record.TagName.Equals("DETAILS", StringComparison.OrdinalIgnoreCase) ? record : record.QuerySelector("details")!;
        Assert.False(details.HasAttribute("open"));
        Assert.Contains("PreflightRecordTitle", record.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithNoPreflightRecord_ShowsNoCard()
    {
        // Runs created before the record existed, and launch paths that run no preflight.
        _handler.SetJsonResponse("api/pipelines/runs/84", new PipelineRunDto
        {
            Id = 84,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 84));

        cut.WaitForAssertion(() => Assert.Contains("aetheus-candidate", cut.Markup));
        Assert.DoesNotContain("run-preflight-check", cut.Markup);
    }

    private void ServeSealedRun(int runId, string deployable, string? blocking = null)
    {
        var outputs = new Dictionary<string, string>
        {
            ["CANDIDATE_ASSURANCE_GRADE"] = "B",
            ["CANDIDATE_DEPLOYABLE"] = deployable
        };
        if (blocking is not null) outputs["CANDIDATE_BLOCKING_TESTS"] = blocking;

        _handler.SetJsonResponse($"api/pipelines/runs/{runId}", new PipelineRunDto
        {
            Id = runId,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1,
                    StageName = "Seal",
                    StepName = "assurance",
                    Status = TaskExecutionStatus.Success,
                    OutputVariables = outputs
                }
            ]
        });
    }

    [Fact]
    public void TheVerdict_IsABadgeOfTheStatusTile_NotABannerAboveTheTabs()
    {
        // The overview status tile carries the compact verdict with detailed tooltip; the header
        // avoids repeating overview values.
        ServeSealedRun(87, "false", "agentCompatibility");

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 87));

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".run-overview-tiles .run-candidate-verdict-badge");
            Assert.Contains("CandidateNotDeployableShort", badge.TextContent, StringComparison.Ordinal);
            Assert.Equal("CandidateNotDeployableTitle", badge.GetAttribute("title"));
        });
        var header = cut.Find(".pipeline-run-page-header");
        Assert.DoesNotContain("CandidateNotDeployable", header.InnerHtml, StringComparison.Ordinal);
        Assert.Empty(header.QuerySelectorAll(".pipeline-name-link"));
        Assert.Empty(cut.FindAll(".run-overview-tiles .pipeline-gate-grade-badge"));

        // Nothing between the header frame and the tab rail: no alert survives there.
        var frame = cut.Find(".pipeline-run-page-header");
        var tabs = cut.Find(".pipeline-run-tabs");
        var between = frame.NextElementSibling;
        while (between is not null && between != tabs)
        {
            Assert.DoesNotContain("rz-alert", between.ClassName ?? string.Empty, StringComparison.Ordinal);
            between = between.NextElementSibling;
        }
    }

    [Fact]
    public void R2_025_TheVerdict_SitsUnderTheStatusBadge_InTheNextRowOfTheStatusTile()
    {
        ServeSealedRun(88, "false", "agentCompatibility");

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 88));

        cut.WaitForAssertion(() => cut.Find(".run-overview-tiles .run-candidate-verdict-badge"));
        // R-28: the status tile is an OmniFactTile; each fact is one of its rows, so the verdict is the
        // row under the one of the run status (it was the second badge of a vertical stack).
        var rows = cut.FindAll(".run-overview-tiles .run-status-tile .omni-fact-tile__row").ToList();
        var statusRow = rows.FindIndex(row => row.QuerySelector(".omni-badge") is not null);
        var verdictRow = rows.FindIndex(row => row.QuerySelector(".run-candidate-verdict-badge") is not null);
        Assert.Equal(0, statusRow);
        Assert.Equal(statusRow + 1, verdictRow);
        Assert.Null(rows[statusRow].QuerySelector(".run-candidate-verdict-badge"));
        Assert.Contains("CandidateNotDeployableShort", rows[verdictRow].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AGreenRunWhoseCandidateIsNotDeployable_SaysSoAtTheTop()
    {
        // The run succeeded and the package is still refused: without this the only trace was a line
        // in a step log.
        ServeSealedRun(85, "false", "agentCompatibility, previousSmoke");

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 85));

        cut.WaitForAssertion(() => Assert.Contains("CandidateNotDeployableTitle", cut.Markup));
        cut.WaitForAssertion(() => Assert.Contains("agentCompatibility", cut.Markup));
    }

    [Fact]
    public void R1_33_ADeployableCandidate_ShowsNoSuccessAlertNorBadge()
    {
        ServeSealedRun(86, "true");

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 86));

        cut.WaitForAssertion(() => Assert.Contains("aetheus-candidate", cut.Markup));
        Assert.DoesNotContain("CandidateDeployableTitle", cut.Markup);
        Assert.DoesNotContain("CandidateDeployableShort", cut.Markup);
        Assert.Empty(cut.FindAll(".run-candidate-verdict-badge"));
        Assert.DoesNotContain("CandidateNotDeployableTitle", cut.Markup);
    }

    [Fact]
    public void ARunThatIsNotACandidate_ShowsNoVerdict()
    {
        ServeRun(87, PipelineStatus.Success, null);

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 87));

        cut.WaitForAssertion(() => Assert.Contains("aetheus-candidate", cut.Markup));
        Assert.DoesNotContain("CandidateDeployableTitle", cut.Markup);
        Assert.DoesNotContain("CandidateNotDeployableTitle", cut.Markup);
    }

    [Fact]
    public void ASealWithAGradeButNoVerdict_IsNotShownAsARefusal()
    {
        // CANDIDATE_DEPLOYABLE can be absent (or unparseable) while a grade is published: the verdict
        // is undecided, not negative, and the header badge must not turn that into "not deployable".
        _handler.SetJsonResponse("api/pipelines/runs/88", new PipelineRunDto
        {
            Id = 88,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1,
                    StageName = "Seal",
                    StepName = "assurance",
                    Status = TaskExecutionStatus.Success,
                    OutputVariables = new Dictionary<string, string> { ["CANDIDATE_ASSURANCE_GRADE"] = "B" }
                }
            ]
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 88));

        cut.WaitForAssertion(() => Assert.Contains("aetheus-candidate", cut.Markup));
        Assert.DoesNotContain("CandidateNotDeployableTitle", cut.Markup);
        Assert.DoesNotContain("CandidateDeployableTitle", cut.Markup);
    }

    [Fact]
    public void AChildOfTheChainSealedIt_TheParentStillShowsTheVerdict()
    {
        // The seal can run inside a triggered child rather than the run being looked at: the parent
        // carries no output variable of its own, only a trigger step pointing at the run that sealed.
        _handler.SetJsonResponse("api/pipelines/runs/88", new PipelineRunDto
        {
            Id = 88,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1,
                    StageName = "Build and test",
                    StepName = "trigger (aetheus-ci)",
                    Status = TaskExecutionStatus.Success,
                    TriggeredRunId = 89
                }
            ]
        });
        _handler.SetJsonResponse("api/pipelines/runs/89", new PipelineRunDto
        {
            Id = 89,
            PipelineId = 6,
            PipelineName = "aetheus-ci",
            Status = PipelineStatus.Success,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 2,
                    StageName = "Seal",
                    StepName = "assurance",
                    Status = TaskExecutionStatus.Success,
                    OutputVariables = new Dictionary<string, string>
                    {
                        ["CANDIDATE_ASSURANCE_GRADE"] = "B",
                        ["CANDIDATE_DEPLOYABLE"] = "false",
                        ["CANDIDATE_BLOCKING_TESTS"] = "agentCompatibility"
                    }
                }
            ]
        });

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 88));

        // The verdict needs the child run's answer, which the page reads after the root run on thread
        // pool continuations: wait with the loaded-machine ceiling, not bUnit's implicit second.
        cut.WaitForAssertion(() => Assert.Contains("CandidateNotDeployableTitle", cut.Markup));
        cut.WaitForAssertion(() => Assert.Contains("agentCompatibility", cut.Markup));
    }
}
