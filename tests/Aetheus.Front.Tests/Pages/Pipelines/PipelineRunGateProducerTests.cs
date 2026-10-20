// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Candidate 2634 showed grade F and "Tests bloquants: currentIntegration=Failed", and nothing said
/// where that came from: every step of its QA child (run 2638) was green, and the four failed tests
/// were in the 5253-line log of « Test V integration ». These pin the trace from the seal's entry to
/// the step that published the non-zero status, and what the page draws of it.
/// </summary>
public sealed class PipelineRunGateProducerTests : BunitContext
{
    private static readonly DateTime T0 = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunGateProducerTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineStepRunDto Step(int id, string name, DateTime? startedAt, Dictionary<string, string>? outputs = null,
        int? triggeredRunId = null, TaskExecutionStatus status = TaskExecutionStatus.Success) => new()
        {
            Id = id,
            StageName = name,
            StepName = name,
            Status = status,
            StartedAt = startedAt,
            TriggeredRunId = triggeredRunId,
            OutputVariables = outputs ?? []
        };

    /// <summary>The shape of 2634/2638: the candidate's trigger step carries the QA outputs copied up,
    /// the seal publishes the verdict, and in the QA run the persisting step re-publishes the status
    /// after the suite step that measured it. The persisting step is listed FIRST, started LATER.</summary>
    private static (PipelineRunDto Candidate, Dictionary<int, PipelineRunDto> Children) Candidate2634(bool withFailedTests = true)
    {
        var integrationOutputs = new Dictionary<string, string> { ["QA_CURRENT_INTEGRATION_GATE_STATUS"] = "1" };
        if (withFailedTests)
            integrationOutputs["QA_CURRENT_INTEGRATION_FAILED_TESTS"] = "Auth.LoginLocksOut, Roles.ReaderCannotWrite, +2 more";
        var qa = new PipelineRunDto
        {
            Id = 2638,
            PipelineName = "aetheus-qa",
            Status = PipelineStatus.Success,
            Steps =
            [
                Step(51, "Persist honest compatibility mode", T0.AddMinutes(30),
                    new() { ["QA_CURRENT_INTEGRATION_GATE_STATUS"] = "1", ["QA_CURRENT_E2E_GATE_STATUS"] = "0" }),
                Step(49, "Deploy V and migrate", T0.AddMinutes(1)),
                Step(50, "Test V integration", T0.AddMinutes(5), integrationOutputs)
            ]
        };
        var candidate = new PipelineRunDto
        {
            Id = 2634,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            Steps =
            [
                Step(10, "Qualify on QA", T0, new() { ["QA_CURRENT_INTEGRATION_GATE_STATUS"] = "1" }, triggeredRunId: 2638),
                Step(11, "Seal the assurance contract", T0.AddMinutes(40), new()
                {
                    ["CANDIDATE_ASSURANCE_GRADE"] = "F",
                    ["CANDIDATE_DEPLOYABLE"] = "false",
                    ["CANDIDATE_BLOCKING_TESTS"] = "currentIntegration=Failed,agentCompatibility=Unavailable"
                })
            ]
        };
        return (candidate, new Dictionary<int, PipelineRunDto> { [qa.Id] = qa });
    }

    [Fact]
    public void TheProducer_IsTheFirstStepThatSetTheStatus_InTheChildTheTriggerStarted()
    {
        var (candidate, children) = Candidate2634();

        var verdict = PipelineRunAssuranceVerdict.Read(candidate, children)!;

        var integration = verdict.BlockingTests[0];
        Assert.Equal("currentIntegration", integration.Key);
        var producer = Assert.IsType<PipelineRunGateProducer.Producer>(integration.Producer);
        // Not the candidate's trigger step (it only waited), not the persisting step (it started later).
        Assert.Equal((2638, "aetheus-qa", 50, "Test V integration"),
            (producer.RunId, producer.PipelineName, producer.StepId, producer.StepName));
        Assert.Equal(["Auth.LoginLocksOut", "Roles.ReaderCannotWrite", "+2 more"], producer.FailedTests);
    }

    [Fact]
    public void ATestNoStepSetNonZero_HasNoProducer_AndKeepsTheSealsWords()
    {
        var (candidate, children) = Candidate2634();

        var agent = PipelineRunAssuranceVerdict.Read(candidate, children)!.BlockingTests[1];

        Assert.Equal("agentCompatibility=Unavailable", agent.Text);
        Assert.Null(agent.Producer);
    }

    [Fact]
    public void AnUnknownKey_OrAStatusOfZero_HasNoProducer()
    {
        var run = new PipelineRunDto
        {
            Id = 1,
            Steps = [Step(1, "x", T0, new() { ["QA_CURRENT_E2E_GATE_STATUS"] = "0" })]
        };

        Assert.Null(PipelineRunGateProducer.StatusVariableOf("somethingElse"));
        Assert.Null(PipelineRunGateProducer.Find("QA_CURRENT_E2E_GATE_STATUS", run, null));
    }

    [Fact]
    public void WithoutTheChildLoaded_TheTriggerStepIsTheProducer()
    {
        var (candidate, _) = Candidate2634();

        var producer = PipelineRunGateProducer.Find("QA_CURRENT_INTEGRATION_GATE_STATUS", candidate, null)!;

        Assert.Equal((2634, 10), (producer.RunId, producer.StepId));
    }

    [Fact]
    public void TheFailedTests_ComeFromAnotherStepOfTheSameRun_WhenTheProducerPublishedNone()
    {
        var run = new PipelineRunDto
        {
            Id = 7,
            PipelineName = "aetheus-ci",
            Steps =
            [
                Step(1, "Verify evidence", T0, new() { ["UNIT_TEST_GATE_STATUS"] = "1" }),
                Step(2, "Name the failures", T0.AddMinutes(1), new() { ["UNIT_TEST_FAILED_TESTS"] = "Back.A, Front.B" })
            ]
        };

        var producer = PipelineRunGateProducer.Find("UNIT_TEST_GATE_STATUS", run, null)!;

        Assert.Equal(1, producer.StepId);
        Assert.Equal(["Back.A", "Front.B"], producer.FailedTests);
        Assert.Equal("QA_PREVIOUS_E2E_FAILED_TESTS", PipelineRunGateProducer.FailedTestsVariableOf("QA_PREVIOUS_E2E_GATE_STATUS"));
    }

    [Fact]
    public void TheList_SaysWhichStepOfWhichRun_LinksToItsLog_AndNamesTheFailedTests()
    {
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.French());
        var (candidate, children) = Candidate2634();
        var verdict = PipelineRunAssuranceVerdict.Read(candidate, children)!;

        var cut = Render<PipelineRunBlockingTests>(parameters => parameters.Add(component => component.Tests, verdict.BlockingTests));

        var lines = cut.FindAll(".run-blocking-test");
        Assert.Equal(2, lines.Count);
        Assert.Contains("currentIntegration : échec, étape « Test V integration » du run #2638 (aetheus-qa)", lines[0].TextContent, StringComparison.Ordinal);
        var link = lines[0].QuerySelector("a")!;
        Assert.Equal("/pipelines/runs/2638?tab=logs&step=50", link.GetAttribute("href"));
        Assert.Contains("Ouvrir les logs", link.TextContent, StringComparison.Ordinal);
        Assert.Contains("Tests en échec: Auth.LoginLocksOut, Roles.ReaderCannotWrite, +2 more",
            lines[0].QuerySelector(".run-blocking-test-failures")!.TextContent, StringComparison.Ordinal);
        // The entry no step can be traced to degrades to the seal's own words, without a link.
        Assert.Equal("agentCompatibility=Unavailable", lines[1].TextContent.Trim());
        Assert.Null(lines[1].QuerySelector("a"));
    }

    [Fact]
    public void WithoutAFailedTestList_TheLineStillLinksToTheStep()
    {
        var (candidate, children) = Candidate2634(withFailedTests: false);
        var verdict = PipelineRunAssuranceVerdict.Read(candidate, children)!;

        var cut = Render<PipelineRunBlockingTests>(parameters => parameters.Add(component => component.Tests, verdict.BlockingTests));

        Assert.Empty(cut.FindAll(".run-blocking-test-failures"));
        Assert.Equal("/pipelines/runs/2638?tab=logs&step=50", cut.Find(".run-blocking-test a").GetAttribute("href"));
    }

    [Fact]
    public void TheGateTabsSealedGradeAlert_CarriesTheTrace()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/analysis/runs/findings",
            new AnalysisRunFindingsPageDto { Items = [], TotalCount = 0, Page = 1, PageSize = 25 });
        var (candidate, children) = Candidate2634();
        var verdict = PipelineRunAssuranceVerdict.Read(candidate, children)!;
        var gate = new AnalysisRunGateDto
        {
            PipelineRunId = 2634,
            Grade = new AnalysisGradeSummaryDto { OverallGrade = AnalysisGrade.A, Completeness = AnalysisGradeCompleteness.Complete }
        };

        var cut = Render<PipelineRunGateFindings>(parameters => parameters
            .Add(component => component.Gate, gate)
            .Add(component => component.RunGrade, AnalysisGrade.F)
            .Add(component => component.BlockingTests, verdict.BlockingTests));

        var alert = cut.Find(".omni-alert");
        Assert.Contains("GateSealedGradeTitle", alert.TextContent, StringComparison.Ordinal);
        // Recette R1-79: the link stays on the run on screen (2634); its log tab finds the step of child run 2638.
        Assert.Equal("/pipelines/runs/2634?tab=logs&step=50", alert.QuerySelector(".run-blocking-test a")!.GetAttribute("href"));
        Assert.Contains("Auth.LoginLocksOut", alert.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimeline_MarksAGreenStepThatPublishedFindings_AndOnlyIt()
    {
        var (_, children) = Candidate2634();
        var qa = children[2638];

        var cut = Render<RunTimelineTree>(parameters => parameters.Add(component => component.Run, qa));

        var marked = cut.FindAll(".run-tl-step").Where(row => row.QuerySelector(".run-tl-gate-findings") is not null).ToList();
        Assert.Equal(2, marked.Count);
        Assert.Contains(marked, row => row.TextContent.Contains("Test V integration", StringComparison.Ordinal));
        Assert.DoesNotContain(marked, row => row.TextContent.Contains("Deploy V and migrate", StringComparison.Ordinal));
        Assert.Contains("omni-badge--warning", marked[0].QuerySelector(".run-tl-gate-findings")!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_81_TheFindingsBadge_SitsBesideTheStatus_OutOfReachOfThePinnedDurations()
    {
        // The sticky duration block is pinned to the right of a row that scrolls sideways; after the name,
        // the badge ran under it and was painted over. Next to the status it stays on the left.
        var (_, children) = Candidate2634();

        var cut = Render<RunTimelineTree>(parameters => parameters.Add(component => component.Run, children[2638]));

        var row = cut.FindAll(".run-tl-step").First(r => r.QuerySelector(".run-tl-gate-findings") is not null);
        var order = row.Children.Select(child => child.ClassList).ToList();
        var status = order.FindIndex(classes => classes.Contains("run-status-badge"));
        var findings = order.FindIndex(classes => classes.Contains("run-tl-gate-findings"));
        var name = order.FindIndex(classes => classes.Contains("run-tl-name"));
        var times = order.FindIndex(classes => classes.Contains("run-tl-times"));
        Assert.True(status < findings && findings < name && name < times, string.Join(" | ", order.Select(c => string.Join('.', c))));
        Assert.Contains("omni-badge--tonal", row.QuerySelector(".run-tl-gate-findings")!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_81_ARejectedStep_IsOneWarningBadge_NeverGreen_AndNoSecondBadgeRepeatsIt()
    {
        var rejected = Step(60, "Test V integration", T0, new() { ["QA_CURRENT_INTEGRATION_GATE_STATUS"] = "1" }) with { IsRejected = true };
        var run = new PipelineRunDto { Id = 2700, PipelineName = "aetheus-qa", Status = PipelineStatus.Rejected, Steps = [rejected] };

        var cut = Render<RunTimelineTree>(parameters => parameters.Add(component => component.Run, run));

        var row = cut.Find(".run-tl-step");
        var badges = row.QuerySelectorAll(".omni-badge");
        var badge = Assert.Single(badges);
        Assert.Contains("omni-badge--warning", badge.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("omni-badge--success", badge.ClassName, StringComparison.Ordinal);
        Assert.Null(row.QuerySelector(".run-tl-gate-findings"));
    }

    [Fact]
    public void AFailedStep_IsNotMarkedAsFindings_ItsRedBadgeAlreadySaysIt()
    {
        Assert.False(PipelineRunGateProducer.HasGateFindings(
            Step(1, "x", T0, new() { ["QA_CURRENT_E2E_GATE_STATUS"] = "1" }, status: TaskExecutionStatus.Failed)));
        Assert.False(PipelineRunGateProducer.HasGateFindings(
            Step(2, "y", T0, new() { ["QA_CURRENT_E2E_GATE_STATUS"] = "0" })));
    }

    [Fact]
    public void TheStepQueryParameter_OpensThatStepInTheLogs()
    {
        var handler = _handler;
        handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
        var (_, children) = Candidate2634();
        handler.SetJsonResponse("api/pipelines/runs/2638", children[2638]);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/runs/2638?tab=logs&step=49");

        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 2638));

        // Without ?step= the page opens the last completed step (50), not the one the link names.
        cut.WaitForAssertion(() => Assert.Equal(49, cut.Instance.SelectedStep?.Id));
        Assert.True(cut.Instance.IsStepSelectionPinned);
    }
}
