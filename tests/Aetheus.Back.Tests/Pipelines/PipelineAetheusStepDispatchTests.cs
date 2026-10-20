// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
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
/// A literal YAML pipeline with a <c>type: aetheus</c> step, taken through parsing, save-time
/// validation and the real dispatcher (real function factory and task builder) down to the task the
/// agent receives. Closes the gap between the factory tests and the YAML the user writes.
/// </summary>
public sealed class PipelineAetheusStepDispatchTests
{
    private const string EncryptedPrefix = "enc:";

    private const string Yaml = """
        name: detect
        stages:
          - name: Build
            agent: linux-01
            steps:
              - name: Detect languages
                type: aetheus
                function: detect-project-languages
                with:
                  root: $(SOURCE_DIR)
        """;

    [Fact]
    public async Task AnAetheusStep_PassesValidation_AndBecomesAFunctionTaskForTheAgent()
    {
        var definition = YamlParsingHelper.ParseAndValidate(Yaml)!;
        var errors = new List<string>();
        PipelineDefinitionValidator.ValidateStages(definition, errors, []);
        Assert.Empty(errors);

        var tracked = new List<ServerTask>();
        var stepRun = new PipelineStepRun { Id = 3, PipelineRunId = 7, StageName = "Build", StepName = "Detect languages" };

        var finalized = await DispatchAsync(YamlParsingHelper.FlattenJobs(definition).Single(), stepRun, tracked);

        Assert.False(finalized);
        var task = Assert.Single(tracked);
        Assert.Equal(OperationKind.PipelineFunction, task.Operation);
        Assert.Equal(626, (int)task.Operation);
        Assert.Equal("detect-project-languages", task.Command);
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
            task.EnvironmentVariables[EncryptedPrefix.Length..])!;
        Assert.True(PipelineFunctionVariables.TryParseParameters(
            environment[PipelineFunctionVariables.ParametersJson], out var parameters));
        Assert.Equal("src", Assert.Single(parameters).Value);
        Assert.Equal("/w", environment["WORKSPACE"]);
        Assert.Equal("7", environment["AETHEUS_RUN_ID"]);
    }

    [Fact]
    public void AnUnknownFunction_IsRefusedAtValidation_NotAtDispatch()
    {
        var definition = YamlParsingHelper.ParseAndValidate(
            Yaml.Replace("detect-project-languages", "no-such-function", StringComparison.Ordinal))!;
        var errors = new List<string>();

        PipelineDefinitionValidator.ValidateStages(definition, errors, []);

        Assert.NotEmpty(errors);
    }

    private static async Task<bool> DispatchAsync(
        PipelineStageDefinition stageDef, PipelineStepRun stepRun, List<ServerTask> tracked)
    {
        var repository = Substitute.For<IPipelineRepository>();
        repository.When(repo => repo.TrackTask(Arg.Any<ServerTask>())).Do(call => tracked.Add(call.Arg<ServerTask>()));
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => EncryptedPrefix + call.Arg<string>());
        var builder = new PipelineStepTaskBuilder(repository, encryption, NullLogger<PipelineStepTaskBuilder>.Instance);
        var dispatcher = new PipelineStepTaskDispatcher(
            repository, Substitute.For<IPipelineDispatchServerResolver>(), builder,
            Substitute.For<IPipelineRunFinalizer>(), Substitute.For<IPipelineTriggerStepCoordinator>(),
            Substitute.For<IPipelineBranchAdvanceStep>(), Substitute.For<IPipelineAnalysisTaskFactory>(),
            Substitute.For<IPipelineDotnetTestTaskFactory>(), new PipelineFunctionTaskFactory(builder, TimeProvider.System),
            Substitute.For<IPipelineGateStatusTaskFactory>(), Substitute.For<IPipelineDeploymentTaskFactory>(),
            Substitute.For<IPipelineHostOperationTaskFactory>(), Substitute.For<IPipelineArtifactTaskFactory>(),
            Substitute.For<IPipelineScannerTaskFactory>(), Substitute.For<IAiTaskService>(), encryption,
            Substitute.For<IAppDeployEnvProvider>(), new ConfigurationBuilder().Build(),
            Substitute.For<ISecretMaskingService>(), TimeProvider.System,
            NullLogger<PipelineStepTaskDispatcher>.Instance);
        var server = new Server { Id = 5, Name = "runner", OsType = OsType.Linux, OrganizationId = 1 };

        return await dispatcher.CreateTasksForStageAsync(
            7, server, [stepRun], stageDef,
            new Dictionary<string, string> { ["WORKSPACE"] = "/w", ["SOURCE_DIR"] = "src" }, [], false, [],
            organizationId: 1, projectId: 2, Substitute.For<IPipelineChildRunLauncher>(),
            TestContext.Current.CancellationToken);
    }
}
