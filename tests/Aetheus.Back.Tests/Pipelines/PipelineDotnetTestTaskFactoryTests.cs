// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// What the control plane hands the agent for a <c>type: dotnet-test</c> step. The step is useless
/// without a status variable to publish under and a place to put its evidence, so a definition
/// missing either is refused at dispatch rather than producing a task that runs and reports nowhere.
/// </summary>
public sealed class PipelineDotnetTestTaskFactoryTests
{
    private readonly IPipelineStepTaskBuilder _taskBuilder = Substitute.For<IPipelineStepTaskBuilder>();

    private PipelineDotnetTestTaskFactory Factory() => new(_taskBuilder);

    private static PipelineStepDefinition Step() => new()
    {
        Name = "Backend tests",
        Type = "dotnet-test",
        TestProject = "tests/Aetheus.Back.Tests",
        ResultsDirectory = "coverage/backend",
        StatusVariable = "BACKEND_STATUS"
    };

    private string? Create(PipelineStepDefinition step) => Factory().CreateDotnetTestTask(
        runId: 42,
        legServer: new Server { Id = 1, Name = "runner" },
        stepRun: new PipelineStepRun { StepName = step.Name },
        stepDef: step,
        legVars: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WORKSPACE"] = "/srv/run/42"
        },
        stageDef: new PipelineStageDefinition { Name = "test" });

    private Dictionary<string, string> CapturedVariables()
    {
        var call = _taskBuilder.ReceivedCalls().Single(
            c => c.GetMethodInfo().Name == nameof(IPipelineStepTaskBuilder.TrackTypedTask));
        return (Dictionary<string, string>)call.GetArguments()[5]!;
    }

    private string CapturedTarget()
    {
        var call = _taskBuilder.ReceivedCalls().Single(
            c => c.GetMethodInfo().Name == nameof(IPipelineStepTaskBuilder.TrackTypedTask));
        return (string)call.GetArguments()[4]!;
    }

    [Fact]
    public void AValidStepDispatchesThePipelineDotnetTestOperation()
    {
        Assert.Null(Create(Step()));

        _taskBuilder.Received(1).TrackTypedTask(
            42, Arg.Any<Server>(), Arg.Any<PipelineStepRun>(), Arg.Any<PipelineStepDefinition>(),
            "tests/Aetheus.Back.Tests", Arg.Any<Dictionary<string, string>>(),
            OperationKind.PipelineDotnetTest);
    }

    [Fact]
    public void TheProjectTravelsAsTheTaskTargetSoItGoesThroughTargetValidation()
    {
        Create(Step());

        Assert.Equal("tests/Aetheus.Back.Tests", CapturedTarget());
    }

    [Fact]
    public void TheStepsConfigurationReachesTheAgentUnderTheSharedVariableNames()
    {
        var step = Step() with
        {
            TrxName = "backend.trx",
            CollectCoverage = true,
            RunSettings = "coverage.runsettings",
            Configuration = "Debug"
        };

        Create(step);
        var variables = CapturedVariables();

        Assert.Equal("coverage/backend", variables[PipelineDotnetTestVariables.ResultsDirectory]);
        Assert.Equal("BACKEND_STATUS", variables[PipelineDotnetTestVariables.StatusVariable]);
        Assert.Equal("backend.trx", variables[PipelineDotnetTestVariables.TrxName]);
        Assert.Equal("true", variables[PipelineDotnetTestVariables.CollectCoverage]);
        Assert.Equal("coverage.runsettings", variables[PipelineDotnetTestVariables.RunSettings]);
        Assert.Equal("Debug", variables[PipelineDotnetTestVariables.Configuration]);
    }

    [Fact]
    public void CoverageIsOffUnlessTheStepAsksForIt()
    {
        Create(Step());

        Assert.Equal("false", CapturedVariables()[PipelineDotnetTestVariables.CollectCoverage]);
    }

    [Fact]
    public void TheRunsWorkspaceIsPassedThroughUntouched()
    {
        Create(Step());

        Assert.Equal("/srv/run/42", CapturedVariables()["WORKSPACE"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AStepWithoutATestProjectIsRefused(string? project)
    {
        var error = Create(Step() with { TestProject = project });

        Assert.Contains("test_project", error!, StringComparison.Ordinal);
        _taskBuilder.DidNotReceiveWithAnyArgs().TrackTypedTask(
            default, default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public void AStepWithoutAResultsDirectoryIsRefused()
    {
        var error = Create(Step() with { ResultsDirectory = null });

        Assert.Contains("results_directory", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePinnedSdkAndToolDirectoriesAreSubstitutedFromTheRunsOutputs()
    {
        var step = Step() with { Dotnet = "$(DOTNET)", ToolPaths = ["$(NODE_BIN_DIR)", "/opt/tools"] };

        Factory().CreateDotnetTestTask(
            42, new Server { Id = 1, Name = "runner" }, new PipelineStepRun { StepName = step.Name }, step,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORKSPACE"] = "/srv/run/42",
                ["DOTNET"] = "/home/agent/.aetheus/dotnet/10.0.100/dotnet",
                ["NODE_BIN_DIR"] = "/home/agent/.aetheus/node/24.4.1/bin"
            },
            new PipelineStageDefinition { Name = "test" });
        var variables = CapturedVariables();

        Assert.Equal("/home/agent/.aetheus/dotnet/10.0.100/dotnet", variables[PipelineDotnetTestVariables.DotnetPath]);
        Assert.Equal(
            "/home/agent/.aetheus/node/24.4.1/bin\n/opt/tools", variables[PipelineDotnetTestVariables.ToolPaths]);
    }

    [Fact]
    public void AnUnpublishedOutputStaysLiteralSoTheAgentRefusesIt()
    {
        // No silent fallback to the host SDK: the literal reaches the agent, which refuses a dotnet that
        // is neither 'dotnet' nor an absolute path.
        Create(Step() with { Dotnet = "$(DOTNET)" });

        Assert.Equal("$(DOTNET)", CapturedVariables()[PipelineDotnetTestVariables.DotnetPath]);
    }

    [Fact]
    public void WithoutToolchainSettingsNoneIsSent()
    {
        Create(Step());
        var variables = CapturedVariables();

        Assert.False(variables.ContainsKey(PipelineDotnetTestVariables.DotnetPath));
        Assert.False(variables.ContainsKey(PipelineDotnetTestVariables.ToolPaths));
    }

    [Fact]
    public void AStepWithoutAStatusVariableIsRefusedBecauseItWouldReportNowhere()
    {
        var error = Create(Step() with { StatusVariable = null });

        Assert.Contains("status_variable", error!, StringComparison.Ordinal);
    }
}
