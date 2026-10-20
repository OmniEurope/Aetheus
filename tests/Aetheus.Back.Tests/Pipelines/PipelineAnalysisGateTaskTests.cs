// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The analysis-gate step: a security or quality scope (any case) becomes one tracked gate task
/// carrying the run and stage, with the leg variables; any other scope is refused with the error the run
/// fails with, and nothing is tracked.</summary>
public sealed class PipelineAnalysisGateTaskTests
{
    private readonly IPipelineStepTaskBuilder _builder = Substitute.For<IPipelineStepTaskBuilder>();
    private readonly Server _server = new() { Id = 3, Name = "runner" };
    private readonly PipelineStepRun _stepRun = new() { Id = 8, StepName = "gate" };
    private readonly PipelineStageDefinition _stage = new() { Name = "Quality" };

    [Theory]
    [InlineData("security", "security")]
    [InlineData("QUALITY", "quality")]
    public void AKnownScope_TracksOneGateTaskWithTheRunAndStage(string scope, string expectedCommand)
    {
        var step = new PipelineStepDefinition { Name = "gate", Type = "analysis-gate", AnalysisScope = scope };
        var legVars = new Dictionary<string, string> { ["WORKSPACE"] = "/w", ["aetheus_run_id"] = "stale" };

        var error = new PipelineAnalysisTaskFactory(_builder).CreateAnalysisGateTask(42, _server, _stepRun, step, legVars, _stage);

        Assert.Null(error);
        _builder.Received(1).TrackTypedTask(
            42, _server, _stepRun, step, expectedCommand,
            Arg.Is<Dictionary<string, string>>(variables =>
                variables["AETHEUS_RUN_ID"] == "42"
                && variables["AETHEUS_STAGE_NAME"] == "Quality"
                && variables["WORKSPACE"] == "/w"
                && variables.Count == 3),
            OperationKind.PipelineEvaluateAnalysisGate, null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("performance")]
    public void AnyOtherScope_IsRefusedAndNothingIsTracked(string? scope)
    {
        var step = new PipelineStepDefinition { Name = "gate", Type = "analysis-gate", AnalysisScope = scope };

        var error = new PipelineAnalysisTaskFactory(_builder).CreateAnalysisGateTask(42, _server, _stepRun, step, [], _stage);

        Assert.Equal("Step 'gate': analysis_scope must be security or quality.", error);
        _builder.DidNotReceiveWithAnyArgs().TrackTypedTask(default, default!, default!, default!, default!, default!, default);
    }
}
