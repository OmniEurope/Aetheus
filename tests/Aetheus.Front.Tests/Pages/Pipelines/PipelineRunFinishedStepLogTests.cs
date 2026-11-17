// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R-21: the log of a step that failed showed 71 of its 89 lines, the end (the real error) missing.
/// A log read while the step ran stops at the lines written so far; once the step is over it must be read
/// again, and the stop explanation must carry the last error line of the log.
/// </summary>
public sealed class PipelineRunFinishedStepLogTests : BunitContext
{
    private const int TaskId = 38254;
    private const string RealError = "error NETSDK1045: The current .NET SDK does not support targeting .NET 11.0.";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunFinishedStepLogTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static List<TaskLogDto> Lines(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i => new TaskLogDto
        {
            Id = i,
            TaskId = TaskId,
            Message = i == 89 ? RealError : $"line {i}",
            Level = i == 89 ? TaskLogLevel.Error : TaskLogLevel.Info,
            Timestamp = DateTime.UtcNow.AddSeconds(i)
        })
    ];

    private static PipelineStepRunDto Failed() => new()
    {
        Id = 1,
        TaskId = TaskId,
        StepName = "Promote/Sign, preflight and publish immutable package set",
        Status = TaskExecutionStatus.Failed,
        FailureCode = TaskFailureCodes.BuildFailed
    };

    [Fact]
    public async Task AFailedStep_ReadWhileItRan_IsReadAgain_AndItsLastLineIsHeld()
    {
        var window = new PipelineRunLogWindow();
        // What the page held while the step ran: the first 71 lines only.
        window.ApplySnapshot(TaskId, Lines(71), PipelineRunLogWindow.LiveWindow);
        _handler.SetJsonResponse($"api/logs/task/{TaskId}", Lines(89));

        await PipelineRunLogView.LoadFailedStepLogsAsync(
            [Failed()], Services.GetRequiredService<ApiClient>(), window);

        Assert.Equal(89, window.Lines[TaskId].Count);
        Assert.Equal(RealError, window.Lines[TaskId][^1].Message);
        Assert.True(window.IsFinalRead(TaskId));
    }

    [Fact]
    public void TheStopExplanation_CarriesTheLastErrorLineOfTheLog()
    {
        var logs = new Dictionary<int, List<TaskLogDto>> { [TaskId] = Lines(89) };

        var line = PipelineStepStopReason.LastErrorLine(Failed(), logs);
        var cut = Render<PipelineStepStopAlert>(parameters => parameters
            .Add(alert => alert.Step, Failed())
            .Add(alert => alert.LastErrorLine, line));

        Assert.Equal(RealError, line);
        Assert.Contains("NETSDK1045", cut.Find("[data-testid=step-stop-last-error]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnErrorLevelLine_TheLastLineSayingErrorIsUsed_AndAHealthyLogGivesNone()
    {
        var plain = new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = TaskId, Message = "build error CS0103: x", Level = TaskLogLevel.Info },
            new() { Id = 2, TaskId = TaskId, Message = "done", Level = TaskLogLevel.Info }
        };
        Assert.Equal("build error CS0103: x",
            PipelineStepStopReason.LastErrorLine(Failed(), new Dictionary<int, List<TaskLogDto>> { [TaskId] = plain }));
        Assert.Null(PipelineStepStopReason.LastErrorLine(Failed(), new Dictionary<int, List<TaskLogDto>> { [TaskId] = [plain[1]] }));
    }
}
