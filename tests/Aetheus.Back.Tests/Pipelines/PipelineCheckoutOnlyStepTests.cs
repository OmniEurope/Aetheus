// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-39: a step that only declares <c>checkout: true</c> has no command, and the clone of the exact revision is
/// the run preparation's. Dispatched, such a step used to become an agent task that only entered the workspace
/// directory and reported success under a name announcing a checkout. It now finishes without a task, as a success
/// that says the preparation did the work; a checkout step with a command of its own is unchanged.
/// </summary>
public sealed class PipelineCheckoutOnlyStepTests
{
    private const string Yaml = """
        name: sample
        stages:
          - name: Checkout
            agent: linux-01
            steps:
              - name: Check out the exact source
                checkout: true
          - name: Build
            agent: linux-01
            steps:
              - name: Build and keep the checkout
                checkout: true
                shell: dotnet build
        """;

    [Fact]
    public async Task ACheckoutOnlyStep_CreatesNoTask_AndFinishesAsASkippedSuccessNamingThePreparation()
    {
        var stageDefinition = StageNamed("Checkout");
        var stepRun = StepRunOf("Check out the exact source", "Checkout");
        var tracked = new List<ServerTask>();

        var refusal = await DispatchAsync(stageDefinition, stepRun, tracked);

        Assert.Null(refusal);
        Assert.Empty(tracked);
        Assert.Equal(TaskExecutionStatus.Success, stepRun.Status);
        Assert.Equal(PipelineStepSkipReasons.CheckoutDoneByPreparation, stepRun.SkippedReason);
        Assert.Equal(0, stepRun.ExitCode);
        Assert.Equal(5, stepRun.ServerId);
        Assert.NotNull(stepRun.StartedAt);
        Assert.NotNull(stepRun.CompletedAt);
        Assert.Null(stepRun.Task);
    }

    [Fact]
    public async Task ACheckoutStepWithItsOwnCommand_StillRunsItAsATask()
    {
        var stepRun = StepRunOf("Build and keep the checkout", "Build");
        var tracked = new List<ServerTask>();

        var refusal = await DispatchAsync(StageNamed("Build"), stepRun, tracked);

        Assert.Null(refusal);
        var task = Assert.Single(tracked);
        Assert.Contains("dotnet build", task.Command, StringComparison.Ordinal);
        Assert.Equal(TaskExecutionStatus.Assigned, stepRun.Status);
        Assert.Null(stepRun.SkippedReason);
    }

    [Theory]
    [InlineData(true, null, null, null, true)]
    [InlineData(true, "", "  ", null, true)]
    [InlineData(true, "echo hi", null, null, false)]
    [InlineData(true, null, "aetheus", null, false)]
    [InlineData(true, null, null, ".pipeline/env.sample", false)]
    [InlineData(false, null, null, null, false)]
    public void OnlyAStepWithNothingButTheCheckoutIsCheckoutOnly(
        bool checkout, string? shell, string? type, string? envTemplate, bool expected)
    {
        var step = new PipelineStepDefinition
        {
            Name = "s",
            Checkout = checkout,
            Shell = shell ?? string.Empty,
            Type = type,
            EnvTemplate = envTemplate
        };

        Assert.Equal(expected, PipelineRunHelpers.IsCheckoutOnlyStep(step));
    }

    private static PipelineStageDefinition StageNamed(string name)
    {
        var definition = YamlParsingHelper.ParseAndValidate(Yaml)!;
        return YamlParsingHelper.FlattenJobs(definition).Single(stage => stage.Name == name);
    }

    private static PipelineStepRun StepRunOf(string stepName, string stageName) =>
        new() { Id = 3, PipelineRunId = 7, StageName = stageName, StepName = stepName };

    private static async Task<StageDispatchRefusal?> DispatchAsync(
        PipelineStageDefinition stageDef, PipelineStepRun stepRun, List<ServerTask> tracked)
    {
        var repository = Substitute.For<IPipelineRepository>();
        repository.When(repo => repo.TrackTask(Arg.Any<ServerTask>())).Do(call => tracked.Add(call.Arg<ServerTask>()));
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        var builder = new PipelineStepTaskBuilder(repository, encryption, NullLogger<PipelineStepTaskBuilder>.Instance);
        var dispatcher = new PipelineStepTaskDispatcher(
            repository, Substitute.For<IPipelineDispatchServerResolver>(), builder,
            Substitute.For<IPipelineTriggerStepCoordinator>(),
            Substitute.For<IPipelineBranchAdvanceStep>(), Substitute.For<IPipelineAnalysisTaskFactory>(),
            Substitute.For<IPipelineDotnetTestTaskFactory>(), Substitute.For<IPipelineFunctionTaskFactory>(),
            Substitute.For<IPipelineGateStatusTaskFactory>(), Substitute.For<IPipelineDeploymentTaskFactory>(),
            Substitute.For<IPipelineHostOperationTaskFactory>(), Substitute.For<IPipelineArtifactTaskFactory>(),
            Substitute.For<IPipelineScannerTaskFactory>(), Substitute.For<IAiTaskService>(), encryption,
            Substitute.For<IAppDeployEnvProvider>(), new ConfigurationBuilder().Build(),
            Substitute.For<ISecretMaskingService>(), TimeProvider.System,
            NullLogger<PipelineStepTaskDispatcher>.Instance);
        var server = new Server { Id = 5, Name = "runner", OsType = OsType.Linux, OrganizationId = 1 };

        return await dispatcher.CreateTasksForStageAsync(
            7, server, [stepRun], stageDef,
            new Dictionary<string, string> { ["WORKSPACE"] = "/w" }, [], false, [],
            organizationId: 1, projectId: 2, Substitute.For<IPipelineChildRunLauncher>(),
            TestContext.Current.CancellationToken);
    }
}
