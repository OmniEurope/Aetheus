// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>A reader who opens a run sees its step's log without waiting for the reads the log does not
/// need (the gate, the releases, the AI results, the pipeline's summary). The selection of the step used to
/// be the last thing the load did, so one slow read among them left "Select a step to view its logs" on a
/// finished run (E2E MobileRunLogs_ScrollInsideTheTerminal, intermittent under load).</summary>
public sealed class PipelineRunLogsBehindOtherReadsTests : BunitContext
{
    private const int RunId = 42;
    private const int TaskId = 300;
    private const string GateRead = "api/analysis/runs/42/result";
    private const string SummaryRead = "api/pipelines/7/runs?page=1";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunLogsBehindOtherReadsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, SummaryRead, []);
        _handler.SetJsonResponse($"api/logs/task/{TaskId}", Lines(120));
        _handler.SetJsonResponse($"api/pipelines/runs/{RunId}", new PipelineRunDto
        {
            Id = RunId,
            PipelineId = 7,
            PipelineName = "CI Build",
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1, StepName = "long-log", StageName = "build", Status = TaskExecutionStatus.Success, TaskId = TaskId
                }
            ],
            ResolvedVariables = [],
            Warnings = []
        });
    }

    [Theory]
    [InlineData(GateRead)]
    [InlineData("api/releases/by-run/")]
    [InlineData("api/ai/results")]
    [InlineData(SummaryRead)]
    public async Task TheStepIsSelectedAndItsLogRead_WhileAnotherReadOfThePageIsStillHeld(string heldRead)
    {
        var release = HoldRead(heldRead);
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"/pipelines/runs/{RunId}?tab=logs");

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, RunId));
        await _handler.WhenGetAsync(heldRead);

        try
        {
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".run-log-line")));
            Assert.False(release.Task.IsCompleted, "The read must still be held when the log shows.");
            Assert.Equal("long-log", cut.Instance.SelectedStep!.StepName);
        }
        finally
        {
            // The held read is answered at the end so the load is not left pending at disposal.
            release.Complete();
        }
    }

    /// <summary>Holds the next read of <paramref name="urlContains"/> until the returned gate is completed,
    /// then answers it empty, as a backend with nothing to show would.</summary>
    private HeldRead HoldRead(string urlContains)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (urlContains == GateRead)
            _handler.SetAsyncJsonResponse<AnalysisRunGateDto?>(HttpMethod.Get, urlContains, async _ => { await gate.Task; return null; });
        else if (urlContains == "api/releases/by-run/")
            _handler.SetAsyncJsonResponse(HttpMethod.Get, urlContains, async _ => { await gate.Task; return new List<ReleaseDto>(); });
        else if (urlContains == "api/ai/results")
            _handler.SetAsyncJsonResponse(HttpMethod.Get, urlContains, async _ => { await gate.Task; return new PaginatedResult<AiRunResultDto>(); });
        else
            _handler.SetAsyncJsonResponse(HttpMethod.Get, urlContains, async _ => { await gate.Task; return new PaginatedResult<PipelineRunDto>(); });
        return new HeldRead(gate);
    }

    private sealed class HeldRead(TaskCompletionSource gate)
    {
        public Task Task => gate.Task;
        public void Complete() => gate.SetResult();
    }

    private static List<TaskLogDto> Lines(int count) =>
        [.. Enumerable.Range(1, count).Select(id => new TaskLogDto
        {
            Id = id,
            TaskId = TaskId,
            Message = $"line {id}",
            Timestamp = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc).AddMilliseconds(id)
        })];
}
