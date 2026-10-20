// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

public class PipelineRunDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(
            HttpMethod.Get,
            "api/ai/results",
            []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
        // The run history: a finished run reads it too now, for the per-step drift against the last run.
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
    }

    private static PipelineRunDto MakeRun(PipelineStatus status, List<PipelineStepRunDto>? steps = null) =>
        new()
        {
            Id = 42,
            PipelineId = 7,
            PipelineName = "CI Build",
            Status = status,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = status == PipelineStatus.Running ? null : DateTime.UtcNow,
            Steps = steps ?? [],
            ResolvedVariables = [],
            Warnings = [],
        };

    [Fact]
    public void Renders_Loading_Initially()
    {
        _handler.SetJsonResponse<PipelineRunDto?>(HttpMethod.Get, "api/pipelines/runs/1", null);
        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 1));
        // The page fetches the run for the routed RunId during initialization.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/pipelines/runs/1"));
    }

    [Fact]
    public void Renders_RunNotFound_WhenNullResponse()
    {
        _handler.SetResponse("api/pipelines/runs", System.Net.HttpStatusCode.NotFound);
        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 99));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular") || cut.Markup.Contains("RunNotFound"));

        // A 404 leaves _run null and the page shows the RunNotFound empty state.
        Assert.Null(typeof(PipelineRun).GetField("_run", Priv)!.GetValue(cut.Instance));
        Assert.Contains("RunNotFound", cut.Markup);
    }

    [Fact]
    public void Renders_RunData_WithSteps()
    {
        var run = MakeRun(PipelineStatus.Success, [
            new PipelineStepRunDto
            {
                Id = 1, StepName = "build", StageName = "Build",
                Status = TaskExecutionStatus.Success,
                StartedAt = DateTime.UtcNow.AddMinutes(-4),
                CompletedAt = DateTime.UtcNow.AddMinutes(-3)
            }
        ]);
        _handler.SetJsonResponse("api/pipelines/runs/42", run);

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 42));
        cut.WaitForState(() => cut.Markup.Contains("CI Build") || !cut.Markup.Contains("rz-progressbar-circular"));

        // The loaded run's pipeline name renders in the header.
        Assert.Contains("CI Build", cut.Markup);
    }

    [Fact]
    public void Renders_FailedRun_WithWarnings()
    {
        var run = MakeRun(PipelineStatus.Failed, [
            new PipelineStepRunDto
            {
                Id = 1, StepName = "deploy", StageName = "Deploy",
                Status = TaskExecutionStatus.Failed
            }
        ]) with
        { Id = 10, Warnings = ["no online server matches agent 'linux'"] };
        _handler.SetJsonResponse("api/pipelines/runs/10", run);

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 10));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // The "no online server matches agent" warning is a blocking warning and is surfaced in the run view.
        Assert.Contains("no online server matches agent", cut.Markup);
    }

    [Fact]
    public void SelectStep_SetsSelectedInSplitView()
    {
        var run = MakeRun(PipelineStatus.Success, [
            new PipelineStepRunDto { Id = 1, StepName = "s1", StageName = "Stage1", Status = TaskExecutionStatus.Success }
        ]) with
        { Id = 5 };
        _handler.SetJsonResponse("api/pipelines/runs/5", run);

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 5));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var selected = typeof(PipelineRun).GetField("_selectedStep", Priv)!;
        Assert.NotNull(selected.GetValue(cut.Instance));
    }

    [Fact]
    public void GetAggregateStatus_ReturnsCorrectStatus()
    {
        var steps = new List<PipelineStepRunDto>
        {
            new() { Id = 1, StepName = "a", StageName = "s", Status = TaskExecutionStatus.Success },
            new() { Id = 2, StepName = "b", StageName = "s", Status = TaskExecutionStatus.Success }
        };
        var result = PipelineRunFormatting.GetAggregateStatus(steps);
        Assert.Equal(TaskExecutionStatus.Success, result);
    }

    [Fact]
    public void GetAggregateStatus_FailedWhenAnyFailed()
    {
        var steps = new List<PipelineStepRunDto>
        {
            new() { Id = 1, StepName = "a", StageName = "s", Status = TaskExecutionStatus.Failed },
            new() { Id = 2, StepName = "b", StageName = "s", Status = TaskExecutionStatus.Success }
        };
        var result = PipelineRunFormatting.GetAggregateStatus(steps);
        Assert.Equal(TaskExecutionStatus.Failed, result);
    }

    [Fact]
    public void FormatDuration_ReturnsCorrectFormat()
    {
        var result = PipelineRunFormatting.FormatDuration(DateTime.UtcNow.AddSeconds(-90), null);
        Assert.Contains("m", result);
    }

    [Fact]
    public void IsBlockingWarning_MatchesCorrectly()
    {
        var blocking = PipelineRunFormatting.IsBlockingWarning("no online server matches agent 'default'");
        var nonBlocking = PipelineRunFormatting.IsBlockingWarning("vault not found");
        Assert.True(blocking);
        Assert.False(nonBlocking);
    }
}
