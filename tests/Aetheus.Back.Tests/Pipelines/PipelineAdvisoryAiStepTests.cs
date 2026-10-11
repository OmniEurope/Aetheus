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
                capture_diff: false
                findings_sarif: .ai-review/findings.sarif
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
        Assert.False(step.CaptureDiff);
        Assert.Equal(".ai-review/findings.sarif", step.FindingsSarif);
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
    [InlineData("shell: echo\n    findings_sarif: a.sarif", true)]
    [InlineData("shell: echo\n    capture_diff: false", true)]
    [InlineData("type: ai\n    profile: p\n    prompt: x\n    advisory: true\n    gate: true", true)]
    [InlineData("type: ai\n    profile: p\n    prompt: x\n    findings_sarif: ../out.sarif", true)]
    [InlineData("type: ai\n    profile: p\n    prompt: x\n    advisory: true\n    capture_diff: false\n    findings_sarif: .ai-review/f.sarif", false)]
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
        var (_, finalizer, stepRun) = await DispatchAiStepAsync(advisory: true);

        Assert.Equal(TaskExecutionStatus.Failed, stepRun.Status);
        Assert.Contains("claude-sonnet", stepRun.FailureReason, StringComparison.Ordinal);
        Assert.StartsWith("Advisory step not run", stepRun.FailureReason, StringComparison.Ordinal);
        await finalizer.DidNotReceive().FailRunWithUnmatchedStagesAsync(
            Arg.Any<int>(), Arg.Any<List<string>>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AMissingProfile_OnAnOrdinaryAiStep_StillFailsTheRun()
    {
        var (_, finalizer, _) = await DispatchAiStepAsync(advisory: false);

        await finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            7, Arg.Is<List<string>>(reasons => reasons.Single().Contains("was not found", StringComparison.Ordinal)),
            Arg.Any<List<PipelineStepRun>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PublicationVariables_CarryTheSwitchesToTheAgent()
    {
        var environment = new Dictionary<string, string>();

        PipelineStepTaskDispatcher.AddAiPublicationVariables(environment, 7, "AI review", new PipelineStepDefinition
        {
            Type = "ai",
            Advisory = true,
            CaptureDiff = false,
            FindingsSarif = ".ai-review/findings.sarif"
        });

        Assert.Equal("true", environment["AETHEUS_AI_ADVISORY"]);
        Assert.Equal("false", environment["AETHEUS_AI_CAPTURE_DIFF"]);
        Assert.Equal(".ai-review/findings.sarif", environment["AETHEUS_AI_FINDINGS_SARIF"]);
        Assert.Equal("7", environment["AETHEUS_RUN_ID"]);
        Assert.Equal("AI review", environment["AETHEUS_STAGE_NAME"]);
    }

    private static async Task<(PipelineStepTaskDispatcher, IPipelineRunFinalizer, PipelineStepRun)> DispatchAiStepAsync(bool advisory)
    {
        var finalizer = Substitute.For<IPipelineRunFinalizer>();
        var ai = Substitute.For<IAiTaskService>();
        ai.BuildPipelineExecutionAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AiExecutionSpec?)null);
        var dispatcher = new PipelineStepTaskDispatcher(
            Substitute.For<IPipelineRepository>(), Substitute.For<IPipelineDispatchServerResolver>(),
            Substitute.For<IPipelineStepTaskBuilder>(), finalizer, Substitute.For<IPipelineTriggerStepCoordinator>(),
            Substitute.For<IPipelineBranchAdvanceStep>(), Substitute.For<IPipelineAnalysisTaskFactory>(),
            Substitute.For<IPipelineDotnetTestTaskFactory>(), Substitute.For<IPipelineGateStatusTaskFactory>(),
            Substitute.For<IPipelineDeploymentTaskFactory>(), Substitute.For<IPipelineHostOperationTaskFactory>(),
            Substitute.For<IPipelineArtifactTaskFactory>(), Substitute.For<IPipelineScannerTaskFactory>(), ai,
            Substitute.For<IEncryptionService>(), Substitute.For<IAppDeployEnvProvider>(),
            new ConfigurationBuilder().Build(), Substitute.For<ISecretMaskingService>(), TimeProvider.System,
            NullLogger<PipelineStepTaskDispatcher>.Instance);
        var stageDef = new PipelineStageDefinition
        {
            Name = "AI review",
            Steps =
            [
                new PipelineStepDefinition
                {
                    Name = "Review commits", Type = "ai", Profile = "claude-sonnet", Prompt = "Review.", Advisory = advisory
                }
            ]
        };
        var stepRun = new PipelineStepRun { Id = 3, PipelineRunId = 7, StageName = "AI review", StepName = "Review commits" };
        var server = new Server { Id = 5, Name = "runner", OsType = OsType.Linux, OrganizationId = 1 };

        await dispatcher.CreateTasksForStageAsync(
            7, server, [stepRun], stageDef, new Dictionary<string, string> { ["WORKSPACE"] = "/w" }, [], false,
            [], organizationId: 1, projectId: 2, Substitute.For<IPipelineChildRunLauncher>(),
            TestContext.Current.CancellationToken);
        return (dispatcher, finalizer, stepRun);
    }
}
