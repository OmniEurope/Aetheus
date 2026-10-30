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
/// <c>advisory: true</c> on a <c>type: ai</c> step: its failure, whatever its cause, never changes the
/// run's status, while a <c>continue_on_error</c> failure still makes the run Partial.
/// </summary>
public sealed class PipelineAdvisoryAiStepTests
{
    private const string Yaml = """
        name: candidate
        stages:
          - name: Build
            steps:
              - name: Compile
                shell: dotnet build
                continue_on_error: true
          - name: AI review
            steps:
              - name: Review commits
                type: ai
                profile: claude-sonnet
                advisory: true
                findings: true
                git_range: $(DELIVERY_BASELINE_SOURCE_SHA)..HEAD
                prompt: Review the commits.
        """;

    private static PipelineYamlDefinition Definition() => YamlParsingHelper.ParseAndValidate(Yaml)!;

    private static PipelineStepRun FailedStep(string stage, string step, string? leg = null) => new()
    {
        StageName = stage,
        StepName = leg is null ? step : $"{step} [{leg}]",
        MatrixLeg = leg,
        Status = TaskExecutionStatus.Failed,
        ContinueOnError = true
    };

    [Fact]
    public void Yaml_MapsTheThreeAiKeys()
    {
        var step = YamlParsingHelper.FlattenJobs(Definition()).Single(stage => stage.Name == "AI review").Steps.Single();

        Assert.True(step.Advisory);
        Assert.True(step.Findings);
        Assert.Equal("$(DELIVERY_BASELINE_SOURCE_SHA)..HEAD", step.GitRange);
        Assert.Null(step.CaptureDiff);
        Assert.Null(step.FindingsSarif);
    }

    [Fact]
    public void AnAdvisoryFailure_LeavesTheRunSuccessful()
    {
        var keys = PipelineRunScheduler.AdvisoryStepKeys(Definition());

        var swallowed = PipelineRunScheduler.HasNonAdvisoryFailure([FailedStep("AI review", "Review commits")], keys);

        Assert.False(swallowed);
        Assert.Equal(PipelineStatus.Success, PipelineRunScheduler.DecideFinalStatus(
            cancelled: false, blockingFailure: false, swallowedFailure: swallowed));
    }

    [Fact]
    public void AnAdvisoryFailure_OnAMatrixLeg_IsStillAdvisory()
    {
        var keys = PipelineRunScheduler.AdvisoryStepKeys(Definition());

        Assert.False(PipelineRunScheduler.HasNonAdvisoryFailure(
            [FailedStep("AI review", "Review commits", leg: "linux")], keys));
    }

    [Fact]
    public void AContinueOnErrorFailure_StillMakesTheRunPartial_EvenBesideAnAdvisoryOne()
    {
        var keys = PipelineRunScheduler.AdvisoryStepKeys(Definition());

        var swallowed = PipelineRunScheduler.HasNonAdvisoryFailure(
            [FailedStep("AI review", "Review commits"), FailedStep("Build", "Compile")], keys);

        Assert.True(swallowed);
        Assert.Equal(PipelineStatus.Partial, PipelineRunScheduler.DecideFinalStatus(
            cancelled: false, blockingFailure: false, swallowedFailure: swallowed));
    }

    [Fact]
    public void RunDto_FlagsOnlyTheAdvisorySteps_FromTheSnapshot()
    {
        var run = new PipelineRun
        {
            Id = 1,
            YamlSnapshot = Yaml,
            StepRuns =
            [
                new PipelineStepRun { Id = 1, StageName = "Build", StepName = "Compile" },
                new PipelineStepRun { Id = 2, StageName = "AI review", StepName = "Review commits" },
                new PipelineStepRun { Id = 3, StageName = "AI review", StepName = "Review commits [linux]", MatrixLeg = "linux" }
            ]
        };

        var steps = PipelineRunDtoMapper.MapRunToDto(run).Steps;

        Assert.False(steps.Single(s => s.StepName == "Compile").IsAdvisory);
        Assert.True(steps.Single(s => s.Id == 2).IsAdvisory);
        Assert.True(steps.Single(s => s.Id == 3).IsAdvisory);
    }

    [Fact]
    public void RunDto_WithoutASnapshot_FlagsNothing()
    {
        var run = new PipelineRun
        {
            Id = 1,
            StepRuns = [new PipelineStepRun { Id = 1, StageName = "AI review", StepName = "Review commits" }]
        };

        Assert.False(PipelineRunDtoMapper.MapRunToDto(run).Steps.Single().IsAdvisory);
    }

    [Fact]
    public void AdvisoryOnANonAiStep_IsNotHonoured()
    {
        // The validator refuses it; the scheduler does not trust a definition that slipped through.
        var definition = YamlParsingHelper.ParseAndValidate("""
            name: p
            stages:
              - name: Deploy
                steps:
                  - name: Ship
                    shell: ./deploy.sh
                    advisory: true
            """)!;

        Assert.Empty(PipelineRunScheduler.AdvisoryStepKeys(definition));
    }

    [Theory]
    [InlineData("shell: echo\n    advisory: true", true)]
    [InlineData("shell: echo\n    findings: true", true)]
    [InlineData("shell: echo\n    git_range: a..b", true)]
    [InlineData("shell: echo\n    findings_sarif: a.sarif", true)]
    [InlineData("shell: echo\n    capture_diff: false", true)]
    [InlineData("type: ai\n    profile: p\n    prompt: x\n    advisory: true\n    gate: true", true)]
    // Retired with the CLI runner: refused, never silently ignored.
    [InlineData("type: ai\n    profile: p\n    prompt: x\n    findings_sarif: .ai-review/f.sarif", true)]
    [InlineData("type: ai\n    profile: p\n    prompt: x\n    capture_diff: false", true)]
    [InlineData("type: ai\n    profile: p\n    prompt: x\n    git_range: ''", true)]
    [InlineData("type: ai\n    profile: p\n    prompt: x\n    advisory: true\n    findings: true\n    git_range: $(DELIVERY_BASELINE_SOURCE_SHA)..HEAD", false)]
    public void Validator_AcceptsTheKeysOnlyOnAnAiStep(string stepBody, bool rejected)
    {
        var step = YamlParsingHelper.Deserializer.Deserialize<PipelineStepDefinition>($"name: s\n    {stepBody}".Replace("\n    ", "\n"));
        var errors = new List<string>();

        PipelineAiStepValidator.Validate(errors, step, "stage 'x'");

        Assert.Equal(rejected, errors.Count > 0);
    }

    [Fact]
    public async Task AMissingProfile_OnAnAdvisoryStep_FailsOnlyThatStep()
    {
        var (_, refusal, stepRun) = await DispatchAiStepAsync(
            advisory: true,
            new AiPipelineExecution(null, "AI runner profile 'claude-sonnet' was not found in the pipeline's organization."));

        Assert.Equal(TaskExecutionStatus.Failed, stepRun.Status);
        Assert.Contains("claude-sonnet", stepRun.FailureReason, StringComparison.Ordinal);
        Assert.StartsWith("Advisory step not run", stepRun.FailureReason, StringComparison.Ordinal);
        Assert.Null(refusal);
    }

    [Fact]
    public async Task AMissingProfile_OnAnAdvisoryStep_RecordsTheServicesReason_WithACategory()
    {
        // The service names the profile itself now; the step carries that text and a failure code, so
        // the run view explains a step that never got a task (no log exists to read).
        var (_, _, stepRun) = await DispatchAiStepAsync(
            advisory: true,
            new AiPipelineExecution(null, "AI runner profile 'claude-sonnet' was not found in the pipeline's organization."));

        Assert.Equal(TaskExecutionStatus.Failed, stepRun.Status);
        Assert.Null(stepRun.TaskId);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, stepRun.FailureCode);
        Assert.Equal(
            "Advisory step not run: Step 'Review commits': AI runner profile 'claude-sonnet' was not found in the pipeline's organization.",
            stepRun.FailureReason);
        Assert.Equal(5, stepRun.ServerId);
    }

    [Fact]
    public async Task ANullOrganization_OnAnAdvisoryStep_FailsOnlyThatStep_SayingSo()
    {
        var (_, refusal, stepRun) = await DispatchAiStepAsync(advisory: true, organizationId: null);

        Assert.Equal(TaskExecutionStatus.Failed, stepRun.Status);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, stepRun.FailureCode);
        Assert.Contains("belongs to no organization", stepRun.FailureReason, StringComparison.Ordinal);
        Assert.Null(refusal);
    }

    [Fact]
    public async Task AMissingProfile_OnAnOrdinaryAiStep_StillFailsTheRun()
    {
        var (_, refusal, stepRun) = await DispatchAiStepAsync(
            advisory: false,
            new AiPipelineExecution(null, "AI runner profile 'claude-sonnet' was not found in the pipeline's organization."));

        Assert.NotNull(refusal);
        Assert.Contains("was not found", refusal.Reason, StringComparison.Ordinal);
        Assert.Same(stepRun, refusal.RefusedStep);
    }

    [Fact]
    public async Task ACliProfile_OnAnAdvisoryStep_FailsThatStepWithTheReconfigureReason()
    {
        var (_, refusal, stepRun) = await DispatchAiStepAsync(
            advisory: true,
            new AiPipelineExecution(null, "AI runner profile 'claude-sonnet' must be reconfigured as an API profile (provider, model and API key)."));

        Assert.Equal(TaskExecutionStatus.Failed, stepRun.Status);
        Assert.Contains("must be reconfigured", stepRun.FailureReason, StringComparison.Ordinal);
        Assert.Null(refusal);
    }

    [Fact]
    public async Task GitRange_IsSubstitutedWithTheRunVariables_AndHandedToTheAgent()
    {
        ServerTask? tracked = null;
        var spec = new AiExecutionSpec("ai-run", 600, new Dictionary<string, string> { ["AETHEUS_AI_API_KEY"] = "sk-x" });

        await DispatchAiStepAsync(
            advisory: true, new AiPipelineExecution(spec, null), "$(BASELINE)..HEAD", task => tracked = task);

        Assert.NotNull(tracked);
        Assert.Equal(OperationKind.AiRun, tracked.Operation);
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(tracked.EnvironmentVariables)!;
        Assert.Equal("0123456789abcdef0123456789abcdef01234567..HEAD", environment["AETHEUS_AI_GIT_RANGE"]);
        Assert.Equal("true", environment["AETHEUS_AI_ADVISORY"]);
    }

    [Fact]
    public async Task AFallbackProfile_IsRecordedOnTheRun_NeverSilent()
    {
        var spec = new AiExecutionSpec("ai-run", 600, new Dictionary<string, string>());
        var repository = Substitute.For<IPipelineRepository>();
        const string Resolution = "Profile 'claude-sonnet' named by the step was not found in the organization. "
            + "Using the pipeline's AI runner profile 'Claude'.";

        await DispatchAiStepAsync(
            advisory: false, new AiPipelineExecution(spec, null, Resolution, IsFallback: true),
            pipelineRepository: repository);

        await repository.Received(1).AppendRunWarningsAsync(
            7, Arg.Is<IReadOnlyCollection<string>>(warnings =>
                StoredProblem.English(warnings.Single()) == $"AI step 'Review commits': {Resolution}"
                && StoredProblem.CodeOf(warnings.Single()) == "AiStepFallback"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AProfileNamedByTheStep_AddsNoRunWarning()
    {
        var spec = new AiExecutionSpec("ai-run", 600, new Dictionary<string, string>());
        var repository = Substitute.For<IPipelineRepository>();

        await DispatchAiStepAsync(
            advisory: false,
            new AiPipelineExecution(spec, null, "Using AI runner profile 'claude-sonnet' named by the step."),
            pipelineRepository: repository);

        await repository.DidNotReceive().AppendRunWarningsAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThePipelinesAssignedProfile_IsHandedToTheResolution()
    {
        var spec = new AiExecutionSpec("ai-run", 600, new Dictionary<string, string>());
        var repository = Substitute.For<IPipelineRepository>();
        repository.GetPipelineRunWithPipelineAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 7, Pipeline = new Pipeline { Id = 1, Name = "p", AiRunnerProfileId = 42 } });
        var ai = Substitute.For<IAiTaskService>();

        await DispatchAiStepAsync(
            advisory: false, new AiPipelineExecution(spec, null, "Using the pipeline's AI runner profile 'Claude'.", true),
            aiService: ai, pipelineRepository: repository);

        await ai.Received(1).BuildPipelineExecutionAsync(
            "claude-sonnet", 42, 1, Arg.Any<string>(), Arg.Any<bool>(), 7, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PublicationVariables_CarryTheSwitchesToTheAgent()
    {
        var environment = new Dictionary<string, string>();

        PipelineStepTaskDispatcher.AddAiPublicationVariables(environment, 7, "AI review", new PipelineStepDefinition
        {
            Type = "ai",
            Advisory = true,
            Findings = true
        });

        Assert.Equal("true", environment["AETHEUS_AI_ADVISORY"]);
        Assert.Equal("true", environment["AETHEUS_AI_FINDINGS"]);
        Assert.Equal("7", environment["AETHEUS_RUN_ID"]);
        Assert.Equal("AI review", environment["AETHEUS_STAGE_NAME"]);
    }

    private static async Task<(PipelineStepTaskDispatcher, StageDispatchRefusal?, PipelineStepRun)> DispatchAiStepAsync(
        bool advisory, AiPipelineExecution? execution = null, string? gitRange = null, Action<ServerTask>? onTracked = null,
        int? organizationId = 1, IAiTaskService? aiService = null, IPipelineRepository? pipelineRepository = null)
    {
        var ai = aiService ?? Substitute.For<IAiTaskService>();
        ai.BuildPipelineExecutionAsync(
                Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(execution ?? new AiPipelineExecution(null, null));
        var repository = pipelineRepository ?? Substitute.For<IPipelineRepository>();
        repository.When(repo => repo.TrackTask(Arg.Any<ServerTask>())).Do(call => onTracked?.Invoke(call.Arg<ServerTask>()));
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var dispatcher = new PipelineStepTaskDispatcher(
            repository, Substitute.For<IPipelineDispatchServerResolver>(),
            Substitute.For<IPipelineStepTaskBuilder>(), Substitute.For<IPipelineTriggerStepCoordinator>(),
            Substitute.For<IPipelineBranchAdvanceStep>(), Substitute.For<IPipelineAnalysisTaskFactory>(),
            Substitute.For<IPipelineDotnetTestTaskFactory>(), Substitute.For<IPipelineFunctionTaskFactory>(),
            Substitute.For<IPipelineGateStatusTaskFactory>(),
            Substitute.For<IPipelineDeploymentTaskFactory>(), Substitute.For<IPipelineHostOperationTaskFactory>(),
            Substitute.For<IPipelineArtifactTaskFactory>(), Substitute.For<IPipelineScannerTaskFactory>(), ai,
            encryption, Substitute.For<IAppDeployEnvProvider>(),
            new ConfigurationBuilder().Build(), Substitute.For<ISecretMaskingService>(), TimeProvider.System,
            NullLogger<PipelineStepTaskDispatcher>.Instance);
        var stageDef = new PipelineStageDefinition
        {
            Name = "AI review",
            Steps =
            [
                new PipelineStepDefinition
                {
                    Name = "Review commits", Type = "ai", Profile = "claude-sonnet", Prompt = "Review.", Advisory = advisory,
                    GitRange = gitRange
                }
            ]
        };
        var stepRun = new PipelineStepRun { Id = 3, PipelineRunId = 7, StageName = "AI review", StepName = "Review commits" };
        var server = new Server { Id = 5, Name = "runner", OsType = OsType.Linux, OrganizationId = 1 };

        var refusal = await dispatcher.CreateTasksForStageAsync(
            7, server, [stepRun], stageDef, new Dictionary<string, string> { ["WORKSPACE"] = "/w", ["BASELINE"] = "0123456789abcdef0123456789abcdef01234567" }, [], false,
            [], organizationId, projectId: 2, Substitute.For<IPipelineChildRunLauncher>(),
            TestContext.Current.CancellationToken);
        return (dispatcher, refusal, stepRun);
    }
}
