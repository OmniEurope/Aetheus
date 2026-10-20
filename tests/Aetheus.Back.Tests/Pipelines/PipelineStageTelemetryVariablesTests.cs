// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The project's telemetry environment reaches a deploy stage (dispatcher) and a blue-green colour
/// (host operation factory) only for a run that belongs to a project; a run without one, or without
/// a pipeline, gets nothing, and a colour whose environment cannot be built still starts.
/// </summary>
public sealed class PipelineStageTelemetryVariablesTests
{
    private const int RunId = 52;
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IAppDeployEnvProvider _deployEnv = Substitute.For<IAppDeployEnvProvider>();
    private readonly Pipeline _pipeline = new() { Id = 6, Name = "deploy" };

    private PipelineStepTaskDispatcher Dispatcher() =>
        new(_repo, Substitute.For<IPipelineDispatchServerResolver>(), Substitute.For<IPipelineStepTaskBuilder>(),
            Substitute.For<IPipelineRunFinalizer>(), Substitute.For<IPipelineTriggerStepCoordinator>(),
            Substitute.For<IPipelineBranchAdvanceStep>(), Substitute.For<IPipelineAnalysisTaskFactory>(),
            Substitute.For<IPipelineDotnetTestTaskFactory>(), Substitute.For<IPipelineFunctionTaskFactory>(),
            Substitute.For<IPipelineGateStatusTaskFactory>(), Substitute.For<IPipelineDeploymentTaskFactory>(),
            Substitute.For<IPipelineHostOperationTaskFactory>(), Substitute.For<IPipelineArtifactTaskFactory>(),
            Substitute.For<IPipelineScannerTaskFactory>(), Substitute.For<IAiTaskService>(), Substitute.For<IEncryptionService>(),
            _deployEnv, new ConfigurationBuilder().Build(), Substitute.For<ISecretMaskingService>(), TimeProvider.System,
            NullLogger<PipelineStepTaskDispatcher>.Instance);

    private PipelineHostOperationTaskFactory HostFactory(ILogger<PipelineHostOperationTaskFactory>? logger = null) =>
        new(_repo, Substitute.For<IPipelineGitService>(), Substitute.For<IEncryptionService>(),
            Substitute.For<ISecretMaskingService>(), new ConfigurationBuilder().Build(), _deployEnv,
            logger ?? NullLogger<PipelineHostOperationTaskFactory>.Instance, TimeProvider.System);

    private void RunOfProject(int? projectId)
    {
        _repo.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = RunId, PipelineId = _pipeline.Id, Pipeline = _pipeline });
        _repo.GetPipelineProjectIdAsync(_pipeline, Arg.Any<CancellationToken>()).Returns(projectId);
        _deployEnv.GetDeployEnvAsync(11, null, RunId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["OTEL_SERVICE_NAME"] = "shop", ["STAGE"] = "from-telemetry" });
    }

    private Task AddFor(bool hostOperation, Dictionary<string, string> variables) =>
        hostOperation
            ? HostFactory().AddTelemetryEnvironmentAsync(RunId, variables, TestContext.Current.CancellationToken)
            : Dispatcher().AddDeployObservabilityVariablesAsync(RunId, variables, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunOfAProject_GetsTheProjectsTelemetryEnvironment_OverridingTheSameNames(bool hostOperation)
    {
        RunOfProject(11);
        var variables = new Dictionary<string, string> { ["STAGE"] = "from-yaml", ["KEEP"] = "1" };

        await AddFor(hostOperation, variables);

        Assert.Equal(
            new Dictionary<string, string> { ["STAGE"] = "from-telemetry", ["KEEP"] = "1", ["OTEL_SERVICE_NAME"] = "shop" },
            variables);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunWithoutAProject_GetsNothing(bool hostOperation)
    {
        RunOfProject(projectId: null);
        var variables = new Dictionary<string, string>();

        await AddFor(hostOperation, variables);

        Assert.Empty(variables);
        await _deployEnv.DidNotReceiveWithAnyArgs().GetDeployEnvAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnknownRun_IsNotAskedForItsProject(bool hostOperation)
    {
        _repo.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>()).Returns((PipelineRun?)null);
        var variables = new Dictionary<string, string>();

        await AddFor(hostOperation, variables);

        Assert.Empty(variables);
        await _repo.DidNotReceiveWithAnyArgs().GetPipelineProjectIdAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AColourWhoseTelemetryCannotBeBuilt_IsLoggedAndStartsWithoutIt()
    {
        RunOfProject(11);
        _deployEnv.GetDeployEnvAsync(11, null, RunId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ingest key store unavailable"));
        var logger = Substitute.For<ILogger<PipelineHostOperationTaskFactory>>();
        var variables = new Dictionary<string, string> { ["KEEP"] = "1" };

        await HostFactory(logger).AddTelemetryEnvironmentAsync(RunId, variables, TestContext.Current.CancellationToken);

        Assert.Equal(new Dictionary<string, string> { ["KEEP"] = "1" }, variables);
        logger.ReceivedWithAnyArgs(1).Log(LogLevel.Warning, default, default(object)!, default, default!);
    }
}
