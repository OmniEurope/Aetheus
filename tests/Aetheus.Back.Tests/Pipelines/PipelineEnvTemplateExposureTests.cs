// SPDX-License-Identifier: EUPL-1.2
using System.Text;
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
/// Who receives a vault secret when a step renders its environment sample (ADR-057), through the real
/// dispatcher, the real step task builder and the real renderer. A secret reaches a deployment in exactly one
/// way beside a step naming it: written into the file its versioned sample asks for, carried by one variable
/// that only a step reading it receives.
/// </summary>
public sealed class PipelineEnvTemplateExposureTests
{
    private const string EncryptedPrefix = "enc:";
    private const string Sample = "DB_PASSWORD=#{DB_PASSWORD}#\nAPPNAME=#{APPNAME}#\n";

    private static PipelineStageDefinition Stage(string shell, string? envTemplate = ".pipeline/configs/env/production.env") =>
        new()
        {
            Name = "Deploy",
            Agent = "linux-01",
            Steps =
            [
                new PipelineStepDefinition { Name = "Compose up", Shell = shell, EnvTemplate = envTemplate },
                new PipelineStepDefinition { Name = "Unrelated", Shell = "echo hello" }
            ]
        };

    private static readonly Dictionary<string, string> RunVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WORKSPACE"] = "/w",
        ["APPNAME"] = "wordmorrow",
        ["DB_PASSWORD"] = "s3cr$t\"pw",
        ["JWT_KEY"] = "jwt-signing-key",
        ["ENCRYPTION_KEY"] = "enc-key"
    };

    private static readonly string[] SecretNames = ["DB_PASSWORD", "JWT_KEY", "ENCRYPTION_KEY"];

    [Fact]
    public async Task TheStepThatReadsTheFile_ReceivesItRendered_AndNoSecretOfTheVault()
    {
        var run = await DispatchAsync(Stage("printf '%s' \"$AETHEUS_ENV_FILE_B64\" | base64 -d > .env"));

        var step = run.EnvironmentOf("Compose up");
        var file = Encoding.UTF8.GetString(Convert.FromBase64String(step["AETHEUS_ENV_FILE_B64"]));
        Assert.Equal("DB_PASSWORD=\"s3cr\\$t\\\"pw\"\nAPPNAME=\"wordmorrow\"\n", file);
        Assert.False(run.Failed);
    }

    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("JWT_KEY")]
    [InlineData("ENCRYPTION_KEY")]
    public async Task NoVaultSecret_IsInTheEnvironmentOfTheStep_EvenTheOneTheSampleNames(string secret)
    {
        var run = await DispatchAsync(Stage("printf '%s' \"$AETHEUS_ENV_FILE_B64\" > .env"));

        Assert.DoesNotContain(secret, run.EnvironmentOf("Compose up").Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASecretTheSampleDoesNotName_IsNotInTheRenderedFile()
    {
        var run = await DispatchAsync(Stage("printf '%s' \"$AETHEUS_ENV_FILE_B64\" > .env"));

        var file = Encoding.UTF8.GetString(Convert.FromBase64String(run.EnvironmentOf("Compose up")["AETHEUS_ENV_FILE_B64"]));
        Assert.DoesNotContain("jwt-signing-key", file, StringComparison.Ordinal);
        Assert.DoesNotContain("enc-key", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepWithoutAnEnvTemplate_ReceivesNeitherTheFileNorTheSecrets()
    {
        var run = await DispatchAsync(Stage("printf '%s' \"$AETHEUS_ENV_FILE_B64\" > .env"));

        var unrelated = run.EnvironmentOf("Unrelated");
        Assert.DoesNotContain("AETHEUS_ENV_FILE_B64", unrelated.Keys);
        Assert.All(SecretNames, secret => Assert.DoesNotContain(secret, unrelated.Keys, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AStepWhoseShellDoesNotReadTheFile_DoesNotReceiveItEither()
    {
        // The save-time validation refuses this shape; the dispatcher still must not hand the file over.
        var run = await DispatchAsync(Stage("echo no file used"));

        Assert.DoesNotContain("AETHEUS_ENV_FILE_B64", run.EnvironmentOf("Compose up").Keys);
    }

    [Fact]
    public async Task ASampleNamingAnUndefinedVariable_FailsTheRunWithTheReason_AndStartsNoTask()
    {
        var run = await DispatchAsync(Stage("echo $AETHEUS_ENV_FILE_B64"), sample: "SMTP_HOST=#{SMTP_HOST}#\n");

        Assert.True(run.Failed);
        Assert.Contains("SMTP_HOST", run.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Compose up", run.TaskNames);
    }

    [Fact]
    public async Task AMissingSample_FailsTheRunAndNamesTheWizard()
    {
        var run = await DispatchAsync(Stage("echo $AETHEUS_ENV_FILE_B64"), sample: null);

        Assert.True(run.Failed);
        Assert.Contains("setup wizard", run.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationRefusesAnEnvTemplateWhoseShellNeverReadsTheFile_AndOneOnATypedStep()
    {
        var errors = new List<string>();
        var definition = new PipelineYamlDefinition
        {
            Name = "deploy",
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Deploy",
                    Agent = "linux-01",
                    Steps =
                    [
                        new PipelineStepDefinition
                            { Name = "No read", Shell = "echo", EnvTemplate = ".pipeline/configs/env/production.env" },
                        new PipelineStepDefinition
                            { Name = "Typed", Type = "smoke", Shell = "$AETHEUS_ENV_FILE_B64", EnvTemplate = ".pipeline/configs/env/production.env" },
                        new PipelineStepDefinition
                            { Name = "Outside", Shell = "$AETHEUS_ENV_FILE_B64", EnvTemplate = "deploy/prod.env" }
                    ]
                }
            ]
        };

        PipelineDefinitionValidator.ValidateStages(definition, errors, []);

        Assert.Contains(errors, error => error.Contains("'No read'", StringComparison.Ordinal) && error.Contains("never reads", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("'Typed'", StringComparison.Ordinal) && error.Contains("plain shell step", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("'Outside'", StringComparison.Ordinal) && error.Contains(".pipeline/configs/", StringComparison.Ordinal));
    }

    private sealed record Dispatched(
        List<ServerTask> Tasks, bool Failed, string? FailureReason)
    {
        public IEnumerable<string> TaskNames => Tasks.Select(task => task.Name);

        public Dictionary<string, string> EnvironmentOf(string stepName) => JsonSerializer.Deserialize<Dictionary<string, string>>(
            Tasks.Single(task => task.Name == stepName).EnvironmentVariables[EncryptedPrefix.Length..])!;
    }

    private static async Task<Dispatched> DispatchAsync(PipelineStageDefinition stage, string? sample = Sample)
    {
        var tracked = new List<ServerTask>();
        var repository = Substitute.For<IPipelineRepository>();
        repository.When(repo => repo.TrackTask(Arg.Any<ServerTask>())).Do(call => tracked.Add(call.Arg<ServerTask>()));
        repository.GetPipelineRunWithPipelineAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 7, Pipeline = new Pipeline { Id = 4, ProjectId = 2 } });
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => EncryptedPrefix + call.Arg<string>());
        var configuration = Substitute.For<IProjectConfigRepositoryService>();
        configuration.ReadAsync(2, ".pipeline/configs/env/production.env", Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(sample);

        var builder = new PipelineStepTaskBuilder(repository, encryption, NullLogger<PipelineStepTaskBuilder>.Instance);
        var dispatcher = new PipelineStepTaskDispatcher(
            repository, Substitute.For<IPipelineDispatchServerResolver>(), builder,
            Substitute.For<IPipelineTriggerStepCoordinator>(), Substitute.For<IPipelineBranchAdvanceStep>(),
            Substitute.For<IPipelineAnalysisTaskFactory>(), Substitute.For<IPipelineDotnetTestTaskFactory>(),
            new PipelineFunctionTaskFactory(builder, TimeProvider.System), Substitute.For<IPipelineGateStatusTaskFactory>(),
            Substitute.For<IPipelineDeploymentTaskFactory>(), Substitute.For<IPipelineHostOperationTaskFactory>(),
            Substitute.For<IPipelineArtifactTaskFactory>(), Substitute.For<IPipelineScannerTaskFactory>(),
            Substitute.For<IAiTaskService>(), encryption, Substitute.For<IAppDeployEnvProvider>(),
            new ConfigurationBuilder().Build(), Substitute.For<ISecretMaskingService>(), TimeProvider.System,
            NullLogger<PipelineStepTaskDispatcher>.Instance,
            new PipelineEnvFilePreparer(repository, configuration, Substitute.For<ISecretMaskingService>()));
        var steps = stage.Steps.Select((step, index) => new PipelineStepRun
        {
            Id = index + 1,
            PipelineRunId = 7,
            StageName = stage.Name,
            StepName = step.Name
        }).ToList();
        var server = new Server { Id = 5, Name = "runner", OsType = OsType.Linux, OrganizationId = 1 };

        var refusal = await dispatcher.CreateTasksForStageAsync(
            7, server, steps, stage, new Dictionary<string, string>(RunVariables, StringComparer.OrdinalIgnoreCase), [], false,
            new HashSet<string>(SecretNames, StringComparer.OrdinalIgnoreCase),
            organizationId: 1, projectId: 2, Substitute.For<IPipelineChildRunLauncher>(),
            TestContext.Current.CancellationToken);

        return new Dispatched(tracked, refusal is not null, refusal?.Reason);
    }
}
