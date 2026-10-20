// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The WAIT half of the parent/child (<c>type: trigger</c>) orchestration. When a child run finishes,
/// the handler mirrors its terminal status onto the still-Running parent step and re-enters
/// AdvanceStageAsync so the parent run can progress. Untested before - a regression would strand
/// parent runs Running forever.
/// </summary>
public sealed class PipelineRunCompletedTriggerHandlerTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRunService _runService = Substitute.For<IPipelineRunService>();
    private readonly PipelineRunCompletedTriggerHandler _sut;

    public PipelineRunCompletedTriggerHandlerTests()
        => _sut = new PipelineRunCompletedTriggerHandler(
            _repo, _runService, NullLogger<PipelineRunCompletedTriggerHandler>.Instance);

    private PipelineStepRun ArrangeWaitingStep(TaskExecutionStatus status = TaskExecutionStatus.Running)
    {
        var step = new PipelineStepRun
        {
            Id = 10,
            PipelineRunId = 5,
            StageName = "deploy",
            StepName = "trigger-child",
            Status = status,
            TriggeredRunId = 99
        };
        _repo.FindStepRunsByTriggeredRunIdAsync(99, Arg.Any<CancellationToken>()).Returns([step]);
        return step;
    }

    [Fact]
    public async Task HandleAsync_ChildSuccess_MirrorsSuccessAndAdvancesStage()
    {
        var step = ArrangeWaitingStep();

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).ResolveCompletedTriggerStepAsync(
            step,
            PipelineStatus.Success,
            Arg.Is<IReadOnlyDictionary<string, string>>(outputs => outputs.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ChildSuccess_PropagatesChildOutputVariablesToParentTriggerStep()
    {
        var step = ArrangeWaitingStep();
        _repo.GetSuccessfulStepOutputsAsync(99, Arg.Any<CancellationToken>()).Returns(
        [
            new StepOutputProjection(
                "Package",
                "Create contract",
                """{"CANDIDATE_ID":"abc123","CANDIDATE_VERSION":"c-source-abc123"}""")
        ]);

        await _sut.HandleAsync(
            new PipelineRunCompletedEvent(99, PipelineStatus.Success),
            ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).ResolveCompletedTriggerStepAsync(
            step,
            PipelineStatus.Success,
            Arg.Is<IReadOnlyDictionary<string, string>>(outputs =>
                outputs["CANDIDATE_ID"] == "abc123"
                && outputs["CANDIDATE_VERSION"] == "c-source-abc123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_80_HandleAsync_ChildRejected_HandsUpWhatItFound()
    {
        var step = ArrangeWaitingStep();
        _repo.GetSuccessfulStepOutputsAsync(99, Arg.Any<CancellationToken>()).Returns(
        [
            new StepOutputProjection("Test V", "Test V integration", """{"QA_CURRENT_INTEGRATION_GATE_STATUS":"1"}""")
        ]);

        await _sut.HandleAsync(
            new PipelineRunCompletedEvent(99, PipelineStatus.Rejected),
            ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).ResolveCompletedTriggerStepAsync(
            step,
            PipelineStatus.Rejected,
            Arg.Is<IReadOnlyDictionary<string, string>>(outputs => outputs["QA_CURRENT_INTEGRATION_GATE_STATUS"] == "1"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    public async Task HandleAsync_ChildNotSuccess_SetsStepFailedExitCode1(PipelineStatus childStatus)
    {
        var step = ArrangeWaitingStep();

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, childStatus), ct: TestContext.Current.CancellationToken);

        await _runService.Received(1).ResolveCompletedTriggerStepAsync(
            step,
            childStatus,
            Arg.Is<IReadOnlyDictionary<string, string>>(outputs => outputs.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StepAlreadyResolved_SkipsAndDoesNotAdvance()
    {
        ArrangeWaitingStep(status: TaskExecutionStatus.Success); // no longer Running

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Failed), ct: TestContext.Current.CancellationToken);

        await _runService.DidNotReceive().ResolveCompletedTriggerStepAsync(
            Arg.Any<PipelineStepRun>(),
            Arg.Any<PipelineStatus>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoWaitingSteps_NoOp()
    {
        _repo.FindStepRunsByTriggeredRunIdAsync(99, Arg.Any<CancellationToken>()).Returns([]);

        await _sut.HandleAsync(new PipelineRunCompletedEvent(99, PipelineStatus.Success), ct: TestContext.Current.CancellationToken);

        await _runService.DidNotReceive().ResolveCompletedTriggerStepAsync(
            Arg.Any<PipelineStepRun>(),
            Arg.Any<PipelineStatus>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }
}
