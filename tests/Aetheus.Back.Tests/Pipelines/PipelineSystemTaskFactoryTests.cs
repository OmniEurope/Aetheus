// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The system steps never reach a runner they cannot run on: an offline cleanup runner that
/// no longer exists dispatches nothing, and a runner refused by the deployment target or the run-level
/// isolation contract fails the run with its reason instead of writing tasks.</summary>
public sealed class PipelineSystemTaskFactoryTests
{
    private const int RunId = 12;

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineDispatchServerResolver _servers = Substitute.For<IPipelineDispatchServerResolver>();
    private readonly IPipelineRunFinalizer _finalizer = Substitute.For<IPipelineRunFinalizer>();
    private readonly PipelineSystemTaskFactory _sut;

    public PipelineSystemTaskFactoryTests()
    {
        _repo.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = RunId, Pipeline = new Pipeline { Name = "CI" } });
        _sut = new PipelineSystemTaskFactory(
            _repo, _servers, _finalizer,
            Substitute.For<IPipelineRunDefinitionParser>(),
            Substitute.For<IPipelineVariableResolver>(),
            Substitute.For<ISecretMaskingService>(),
            Substitute.For<IAuditService>(),
            Substitute.For<IEncryptionService>(),
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<PipelineSystemTaskFactory>.Instance);
    }

    [Fact]
    public async Task CleanupForAnAffinityRunnerThatNoLongerExists_DispatchesNothing()
    {
        _repo.GetRunAffinityServerIdAsync(RunId, Arg.Any<CancellationToken>()).Returns(5);

        var dispatched = await CreateAsync(Step(PipelineRunHelpers.SystemCleanupStage), []);

        Assert.False(dispatched);
        await _repo.Received(1).FindServerByIdAsync(5, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunnerRefusedByTheLocalDeploymentTarget_FailsTheRunWithTheReason()
    {
        GivenAnyOnlineRunner(new Server { Id = 3, Name = "build-1", Hostname = "build-1" });
        var steps = Step(PipelineRunHelpers.SystemPrepareStage);

        var dispatched = await CreateAsync(steps, new()
        {
            [PipelineDeploymentTargetGuard.TargetVariable] = PipelineDeploymentTargetGuard.Local,
            [PipelineDeploymentTargetGuard.LocalAgentVariable] = "deploy-host",
        });

        Assert.False(dispatched);
        await _finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            RunId,
            Arg.Is<List<string>>(reasons => reasons.Single().Contains("does not match selector 'deploy-host'", StringComparison.Ordinal)),
            steps,
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContainersOnlyRunnerForAProcessRun_FailsTheRunWithTheReason()
    {
        GivenAnyOnlineRunner(new Server { Id = 4, Name = "locked", Hostname = "locked", RequireContainerIsolation = true });
        var steps = Step(PipelineRunHelpers.SystemPrepareStage);

        var dispatched = await CreateAsync(steps, []);

        Assert.False(dispatched);
        await _finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            RunId,
            Arg.Is<List<string>>(reasons => reasons.Single().Contains("enforces a containers-only policy", StringComparison.Ordinal)),
            steps,
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportedExternalPipeline_CheckoutOnARunnerNotLinkedToTheProject_DispatchesNothing()
    {
        // R-11 (decision of 2026-11-10): the system steps of an imported pipeline run on a server linked to its
        // project only. The organization's any-runner fallback returns an unlinked one; the project has none online.
        _repo.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = RunId, Pipeline = new Pipeline { Name = "oe1-ci", ExternalImportCommit = new string('a', 40) } });
        GivenAnyOnlineRunner(new Server { Id = 3, Name = "org-runner", Hostname = "org-runner" });
        _repo.GetProjectLinkedServerIdsAsync(7, Arg.Any<CancellationToken>()).Returns([8]);

        var dispatched = await _sut.CreateSystemTasksAsync(
            RunId, Step(PipelineRunHelpers.SystemPrepareStage), [], new PipelineYamlDefinition(), organizationId: null,
            projectId: 7, completedStages: [], TestContext.Current.CancellationToken);

        Assert.False(dispatched);
        await _servers.Received(1).ResolveProjectServerForTargetAsync(
            Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(), null, false, 7, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAndAffinityTheCallerAlreadyRead_AreNotReadAgain()
    {
        GivenAnyOnlineRunner(new Server { Id = 4, Name = "locked", Hostname = "locked", RequireContainerIsolation = true });
        var run = new PipelineRun { Id = RunId, Pipeline = new Pipeline { Name = "CI" } };

        await _sut.CreateSystemTasksAsync(
            RunId, Step(PipelineRunHelpers.SystemPrepareStage), [], new PipelineYamlDefinition(), organizationId: null,
            projectId: null, completedStages: [], TestContext.Current.CancellationToken, run, new KnownRunAffinity(null));

        await _repo.DidNotReceive().GetPipelineRunWithPipelineAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        // The runner was still chosen and checked: the known "no affinity" sent it to the any-runner lookup.
        await _repo.Received(1).FindAnyOnlineRunnerAsync(Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AKnownAffinity_IsUsedWithoutReadingItAgain()
    {
        var run = new PipelineRun { Id = RunId, Pipeline = new Pipeline { Name = "CI" } };

        var dispatched = await _sut.CreateSystemTasksAsync(
            RunId, Step(PipelineRunHelpers.SystemCleanupStage), [], new PipelineYamlDefinition(), organizationId: null,
            projectId: null, completedStages: [], TestContext.Current.CancellationToken, run, new KnownRunAffinity(5));

        Assert.False(dispatched);
        await _repo.DidNotReceive().GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).FindServerByIdAsync(5, Arg.Any<CancellationToken>());
    }

    private void GivenAnyOnlineRunner(Server server) =>
        _repo.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>()).Returns(server);

    private static List<PipelineStepRun> Step(string stage) =>
        [new PipelineStepRun { Id = 1, StageName = stage, StepName = "system", IsSystem = true }];

    private Task<bool> CreateAsync(List<PipelineStepRun> steps, Dictionary<string, string> variables) =>
        _sut.CreateSystemTasksAsync(
            RunId, steps, variables, new PipelineYamlDefinition(), organizationId: null, projectId: null,
            completedStages: [], TestContext.Current.CancellationToken);
}
