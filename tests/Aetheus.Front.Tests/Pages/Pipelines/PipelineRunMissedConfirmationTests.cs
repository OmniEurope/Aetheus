// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-22: a run that stopped on a missed stage confirmation shows "retry failed steps" as a
/// button of its header, not as an entry of the page menu; any other failed run keeps it in the menu.</summary>
public sealed class PipelineRunMissedConfirmationTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunMissedConfirmationTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
    }

    private static string Stored(string code, string stage) =>
        StoredProblem.Compose(
            new ProblemDto(code, new Dictionary<string, string> { ["stage"] = stage }),
            $"Stage '{stage}' was not confirmed.");

    private IRenderedComponent<PipelineRun> RenderRun(params string[] warnings)
    {
        _handler.SetJsonResponse("api/pipelines/runs/40", new PipelineRunDto
        {
            Id = 40,
            PipelineId = 5,
            PipelineName = "release-fast",
            Status = PipelineStatus.Failed,
            StartedAt = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 3, 10, 5, 0, DateTimeKind.Utc),
            Warnings = [.. warnings],
            Steps = [new PipelineStepRunDto { Id = 1, StageName = "Confirm", StepName = "wait", Status = TaskExecutionStatus.Failed }]
        });
        var cut = Render<PipelineRun>(parameters => parameters.Add(page => page.RunId, 40));
        cut.WaitForState(() => cut.Markup.Contains("release-fast", StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public void ATimedOutConfirmation_PutsRetryFailedStepsInPlainSightAsTheMainAction()
    {
        var cut = RenderRun(Stored("StageConfirmationTimedOut", "Confirm"));

        var retry = cut.Find("button.run-retry-failed");
        Assert.Contains("RerunFailedSteps", retry.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureWithoutAMissedConfirmation_KeepsTheRetryOutOfTheHeaderButtons()
    {
        var cut = RenderRun();

        Assert.Empty(cut.FindAll("button.run-retry-failed"));
    }

    [Fact]
    public void ARefusedConfirmationOfAnotherStage_DoesNotPinTheRetry()
    {
        var cut = RenderRun(Stored("StageConfirmationTimedOut", "Other"));

        Assert.Empty(cut.FindAll("button.run-retry-failed"));
    }
}
