// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

// Coverage for the run-completion normalisation guard added on the PipelineRun page:
// an orphaned run (terminal status but a null CompletedAt) derives its end time from the
// last finished step, while a genuinely in-progress run keeps CompletedAt null and is
// treated as live (RunInProgress == true).
public class PipelineRunCompletionTests : BunitContext
{
    public PipelineRunCompletionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
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

    // --- NormalizeRunCompletion (pure static guard) ---

    [Theory]
    [InlineData(PipelineStatus.Success)]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    public void NormalizeRunCompletion_TerminalStatus_NullCompletedAt_DerivesMaxStepEnd(PipelineStatus status)
    {
        var firstEnd = new DateTime(2026, 6, 24, 10, 5, 0, DateTimeKind.Utc);
        var lastEnd = new DateTime(2026, 6, 24, 10, 12, 0, DateTimeKind.Utc);

        var run = new PipelineRunDto
        {
            Id = 1,
            PipelineName = "Orphaned",
            Status = status,
            StartedAt = new DateTime(2026, 6, 24, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = null,
            Steps =
            [
                new PipelineStepRunDto { Id = 1, StepName = "A", StageName = "build", Status = TaskExecutionStatus.Success, CompletedAt = firstEnd },
                new PipelineStepRunDto { Id = 2, StepName = "B", StageName = "build", Status = TaskExecutionStatus.Failed, CompletedAt = lastEnd }
            ]
        };

        var normalized = PipelineRunFormatting.NormalizeRunCompletion(run);

        // The derived end time is the MAX of the steps' CompletedAt, not the first.
        Assert.Equal(lastEnd, normalized.CompletedAt);
    }

    [Fact]
    public void NormalizeRunCompletion_PreservesExistingCompletedAt()
    {
        var existing = new DateTime(2026, 6, 24, 9, 0, 0, DateTimeKind.Utc);
        var laterStepEnd = new DateTime(2026, 6, 24, 11, 0, 0, DateTimeKind.Utc);

        var run = new PipelineRunDto
        {
            Id = 2,
            PipelineName = "Already finished",
            Status = PipelineStatus.Success,
            CompletedAt = existing,
            Steps =
            [
                new PipelineStepRunDto { Id = 1, StepName = "A", StageName = "build", Status = TaskExecutionStatus.Success, CompletedAt = laterStepEnd }
            ]
        };

        var normalized = PipelineRunFormatting.NormalizeRunCompletion(run);

        // A run that already has its own CompletedAt is left untouched (not overwritten by step end).
        Assert.Equal(existing, normalized.CompletedAt);
    }

    [Theory]
    [InlineData(PipelineStatus.Running)]
    [InlineData(PipelineStatus.Pending)]
    public void NormalizeRunCompletion_NonTerminalStatus_LeavesCompletedAtNull(PipelineStatus status)
    {
        var run = new PipelineRunDto
        {
            Id = 3,
            PipelineName = "In progress",
            Status = status,
            CompletedAt = null,
            Steps =
            [
                // A finished step end exists, but the run is not terminal, so it must NOT be adopted.
                new PipelineStepRunDto { Id = 1, StepName = "A", StageName = "build", Status = TaskExecutionStatus.Success, CompletedAt = new DateTime(2026, 6, 24, 10, 5, 0, DateTimeKind.Utc) }
            ]
        };

        var normalized = PipelineRunFormatting.NormalizeRunCompletion(run);

        Assert.Null(normalized.CompletedAt);
    }

    [Fact]
    public void NormalizeRunCompletion_TerminalStatus_NoFinishedSteps_StaysNull()
    {
        var run = new PipelineRunDto
        {
            Id = 4,
            PipelineName = "Terminal, no step ends",
            Status = PipelineStatus.Cancelled,
            CompletedAt = null,
            Steps =
            [
                new PipelineStepRunDto { Id = 1, StepName = "A", StageName = "build", Status = TaskExecutionStatus.Pending, CompletedAt = null }
            ]
        };

        var normalized = PipelineRunFormatting.NormalizeRunCompletion(run);

        // No step ever completed -> nothing to derive from -> CompletedAt remains null.
        Assert.Null(normalized.CompletedAt);
    }

    // --- RunInProgress (component state, driven through a rendered run) ---

    private static bool ReadRunInProgress(PipelineRun instance)
    {
        var prop = typeof(PipelineRun).GetProperty("RunInProgress", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)prop.GetValue(instance)!;
    }

    [Fact]
    public void RunInProgress_TerminalRun_WithDerivedCompletion_IsFalse()
    {
        // Terminal status + null CompletedAt: after NormalizeRunCompletion runs on load the run
        // gains an end time, so the page must not treat it as live.
        _handler.SetJsonResponse("api/pipelines/runs/30", new PipelineRunDto
        {
            Id = 30,
            PipelineName = "Orphaned Run",
            Status = PipelineStatus.Failed,
            StartedAt = new DateTime(2026, 6, 24, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = null,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1, StepName = "Build", StageName = "build", Status = TaskExecutionStatus.Failed,
                    StartedAt = new DateTime(2026, 6, 24, 10, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 6, 24, 10, 7, 0, DateTimeKind.Utc)
                }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 30));
        cut.WaitForState(() => cut.Markup.Contains("Orphaned Run"));

        Assert.False(ReadRunInProgress(cut.Instance));

        // And the loaded run carries the step-derived end time (it renders as a finished run).
        var runField = typeof(PipelineRun).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var run = (PipelineRunDto)runField.GetValue(cut.Instance)!;
        Assert.Equal(new DateTime(2026, 6, 24, 10, 7, 0, DateTimeKind.Utc), run.CompletedAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void RunInProgress_NonTerminalRun_IsTrue()
    {
        // Running status + null CompletedAt: stays in progress and CompletedAt is not derived.
        _handler.SetJsonResponse("api/pipelines/runs/31", new PipelineRunDto
        {
            Id = 31,
            PipelineName = "Live Run",
            Status = PipelineStatus.Running,
            StartedAt = new DateTime(2026, 6, 24, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = null,
            Steps =
            [
                new PipelineStepRunDto
                {
                    Id = 1, StepName = "Build", StageName = "build", Status = TaskExecutionStatus.Success,
                    StartedAt = new DateTime(2026, 6, 24, 10, 0, 0, DateTimeKind.Utc),
                    CompletedAt = new DateTime(2026, 6, 24, 10, 3, 0, DateTimeKind.Utc)
                },
                new PipelineStepRunDto
                {
                    Id = 2, StepName = "Test", StageName = "build", Status = TaskExecutionStatus.Running,
                    StartedAt = new DateTime(2026, 6, 24, 10, 3, 0, DateTimeKind.Utc)
                }
            ]
        });

        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 31));
        cut.WaitForState(() => cut.Markup.Contains("Live Run"));

        Assert.True(ReadRunInProgress(cut.Instance));

        // A live run keeps a null CompletedAt despite having a finished step end available.
        var runField = typeof(PipelineRun).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var run = (PipelineRunDto)runField.GetValue(cut.Instance)!;
        Assert.Null(run.CompletedAt);
    }
}
