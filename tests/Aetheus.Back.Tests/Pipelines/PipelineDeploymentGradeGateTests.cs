// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// R-368: a release graded F started a deployment whose declared threshold is E. The threshold only
/// existed as a variable read by a script in aetheus-deploy-prod's second stage (Verify candidate),
/// while the first stage (Restore candidate) is bound to <c>environment: prod</c>: the control plane
/// therefore asked for the production approval, then ran the System prepare and the restore on the
/// production agent, before anything compared the grade with the threshold. The grade gate is now
/// evaluated by the control plane before the first dispatch of the run, so before any approval and any
/// action on a target, and a mismatch ends the attempt at once with the grade and the threshold named.
/// </summary>
public class PipelineDeploymentGradeGateTests
{
    private const int RunId = 9368;
    private const int ProjectId = 3;
    private const int ProdEnvironmentId = 1;
    private const string Candidate = "c-0123456789abcdef0123456789abcdef01234567";

    private static Dictionary<string, string> DeployVariables(string threshold = "E") => new(StringComparer.OrdinalIgnoreCase)
    {
        ["AETHEUS_DEPLOY_MINIMUM_GRADE"] = threshold,
        ["AETHEUS_CANDIDATE_VERSION"] = Candidate
    };

    [Fact]
    public async Task AnFRelease_BelowAnEThreshold_FailsTheRunBeforeAnyApprovalIsRequested()
    {
        var h = new Harness(new DeploymentGateRelease(41, Candidate, AnalysisGrade.F));

        await h.DispatchAsync(DeployVariables());

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await h.Repo.DidNotReceive().TryTransitionPipelineRunStatusAsync(
            Arg.Any<int>(), Arg.Any<PipelineStatus>(), PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>());
        await h.Finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            RunId,
            Arg.Is<List<string>>(reasons => reasons.Count == 1
                && reasons[0].Contains("grade F", StringComparison.Ordinal)
                && reasons[0].Contains("threshold E", StringComparison.Ordinal)
                && reasons[0].Contains(Candidate, StringComparison.Ordinal)),
            Arg.Any<List<PipelineStepRun>>(),
            Arg.Any<CancellationToken>());
        Assert.All(h.PendingSteps, step => Assert.Equal(TaskExecutionStatus.Cancelled, step.Status));
    }

    [Fact]
    public async Task AnFRelease_NeverReachesTheProductionAgent_NotEvenForTheSystemPrepare()
    {
        var h = new Harness(new DeploymentGateRelease(41, Candidate, AnalysisGrade.F), withWorkspace: true);

        await h.DispatchAsync(DeployVariables());

        await h.SystemTasks.DidNotReceive().CreateSystemTasksAsync(
            Arg.Any<int>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>());
        await h.Finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            RunId, Arg.Any<List<string>>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AnalysisGrade.A)]
    [InlineData(AnalysisGrade.E)]
    public async Task AReleaseWithinTheThreshold_ProceedsToTheEnvironmentApproval(AnalysisGrade grade)
    {
        var h = new Harness(new DeploymentGateRelease(41, Candidate, grade));

        await h.DispatchAsync(DeployVariables());

        await h.Finalizer.DidNotReceive().FailRunWithUnmatchedStagesAsync(
            Arg.Any<int>(), Arg.Any<List<string>>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<CancellationToken>());
        await h.Repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.EnvironmentId == ProdEnvironmentId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AReleaseWithoutASealedGrade_IsRefused()
    {
        var h = new Harness(new DeploymentGateRelease(41, Candidate, null));

        await h.DispatchAsync(DeployVariables());

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await h.Finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            RunId,
            Arg.Is<List<string>>(reasons => reasons.Single().Contains("no sealed assurance grade", StringComparison.Ordinal)
                && reasons.Single().Contains("threshold E", StringComparison.Ordinal)),
            Arg.Any<List<PipelineStepRun>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnknownRelease_IsRefused()
    {
        var h = new Harness(release: null);

        await h.DispatchAsync(DeployVariables());

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await h.Finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            RunId,
            Arg.Is<List<string>>(reasons => reasons.Single().Contains(Candidate, StringComparison.Ordinal)
                && reasons.Single().Contains("threshold E", StringComparison.Ordinal)),
            Arg.Any<List<PipelineStepRun>>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("G")]
    [InlineData("4")]
    [InlineData("e")]
    public async Task AThresholdThatIsNotAGrade_IsRefusedRatherThanIgnored(string threshold)
    {
        var h = new Harness(new DeploymentGateRelease(41, Candidate, AnalysisGrade.A));

        await h.DispatchAsync(DeployVariables(threshold));

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await h.Finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            RunId, Arg.Is<List<string>>(reasons => reasons.Single().Contains($"'{threshold}'", StringComparison.Ordinal)),
            Arg.Any<List<PipelineStepRun>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("$(candidateVersion)")]
    public async Task AThresholdWithNoReleaseToCheck_IsRefused(string candidate)
    {
        var h = new Harness(new DeploymentGateRelease(41, Candidate, AnalysisGrade.A));
        var variables = DeployVariables();
        variables["AETHEUS_CANDIDATE_VERSION"] = candidate;

        await h.DispatchAsync(variables);

        await h.Repo.DidNotReceive().AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
        await h.Finalizer.Received(1).FailRunWithUnmatchedStagesAsync(
            RunId, Arg.Any<List<string>>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APipelineWithoutAThreshold_IsNotLookedUp()
    {
        var h = new Harness(new DeploymentGateRelease(41, Candidate, AnalysisGrade.F));

        await h.DispatchAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_CANDIDATE_VERSION"] = Candidate
        });

        await h.Repo.DidNotReceive().FindDeploymentGateReleaseAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Repo.Received(1).AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The gate decides whether a deployment may START. Once a stage has run, failing the run
    /// outright would skip its failed() rollback, so a started run is not re-judged.</summary>
    [Fact]
    public async Task AStartedRun_IsNotReJudged()
    {
        var h = new Harness(new DeploymentGateRelease(41, Candidate, AnalysisGrade.F), completedStages: ["Restore candidate"]);

        await h.DispatchAsync(DeployVariables());

        await h.Repo.DidNotReceive().FindDeploymentGateReleaseAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await h.Finalizer.DidNotReceive().FailRunWithUnmatchedStagesAsync(
            Arg.Any<int>(), Arg.Any<List<string>>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public IPipelineRepository Repo { get; } = Substitute.For<IPipelineRepository>();
        public IPipelineRunFinalizer Finalizer { get; } = Substitute.For<IPipelineRunFinalizer>();
        public IPipelineSystemTaskFactory SystemTasks { get; } = Substitute.For<IPipelineSystemTaskFactory>();
        public List<PipelineStepRun> PendingSteps { get; }
        private readonly PipelineYamlDefinition _definition;
        private readonly PipelineStageDispatchPlanner _planner;

        public Harness(DeploymentGateRelease? release, bool withWorkspace = false, List<string>? completedStages = null)
        {
            // The two stages that matter in aetheus-deploy-prod: the first one is already bound to the
            // production environment, the grade check came only in the second.
            var restore = new PipelineStageDefinition
            {
                Name = "Restore candidate",
                Environment = "prod",
                ExecutionRole = "deploy",
                Steps = withWorkspace ? [new PipelineStepDefinition { Name = "Restore", Shell = "true" }] : []
            };
            var verify = new PipelineStageDefinition
            {
                Name = "Verify candidate",
                Environment = "prod",
                ExecutionRole = "deploy",
                DependsOn = ["Restore candidate"]
            };
            _definition = new PipelineYamlDefinition { Name = "aetheus-deploy-prod", Stages = [restore, verify] };
            PendingSteps = completedStages is { Count: > 0 }
                ? [new PipelineStepRun { StageName = "Verify candidate", Status = TaskExecutionStatus.Pending }]
                : withWorkspace
                    ? [new PipelineStepRun { StageName = PipelineRunService.SystemPrepareStage, Status = TaskExecutionStatus.Pending },
                       new PipelineStepRun { StageName = "Restore candidate", Status = TaskExecutionStatus.Pending }]
                    : [new PipelineStepRun { StageName = "Restore candidate", Status = TaskExecutionStatus.Pending }];

            Repo.HasActiveArtifactCollectionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            Repo.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(completedStages ?? []);
            Repo.GetTerminalStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(completedStages ?? []);
            Repo.HasAnyFailedStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            Repo.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
            Repo.GetActiveStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);
            Repo.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.GetApprovalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns([]);
            var pipeline = new Pipeline { Id = 77, Name = "aetheus-deploy-prod", ProjectId = ProjectId };
            Repo.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>())
                .Returns(new PipelineRun { Id = RunId, PipelineId = pipeline.Id, Pipeline = pipeline, Status = PipelineStatus.Running });
            Repo.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>()).Returns(ProjectId);
            Repo.FindDeploymentGateReleaseAsync(ProjectId, Candidate, Arg.Any<CancellationToken>()).Returns(release);
            Repo.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(new Aetheus.Back.Data.Entities.Environment
                {
                    Id = ProdEnvironmentId,
                    Name = "prod",
                    RequireApproval = true,
                    ApprovalTimeoutMinutes = 1440
                });
            Repo.TryTransitionPipelineRunStatusAsync(
                Arg.Any<int>(), PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>())
                .Returns(true);
            SystemTasks.CreateSystemTasksAsync(
                Arg.Any<int>(), Arg.Any<List<PipelineStepRun>>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
                .Returns(true);
            Finalizer.IsRunActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
            Finalizer.When(f => f.MarkStepsAs(Arg.Any<List<PipelineStepRun>>(), Arg.Any<TaskExecutionStatus>()))
                .Do(call =>
                {
                    foreach (var step in call.Arg<List<PipelineStepRun>>()) step.Status = call.Arg<TaskExecutionStatus>();
                });

            var environmentChecks = Substitute.For<IPipelineEnvironmentCheckGuard>();
            environmentChecks.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                .Returns(true);
            var servers = Substitute.For<IPipelineDispatchServerResolver>();
            servers.ResolveEffectiveStageTarget(Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
                .Returns(callInfo => callInfo.Arg<PipelineStageDefinition>());

            _planner = new PipelineStageDispatchPlanner(
                Repo, servers, environmentChecks, Finalizer, SystemTasks,
                Substitute.For<IPipelineStepTaskDispatcher>(),
                Substitute.For<IHubContext<PipelineHub>>(),
                Substitute.For<IDomainEventDispatcher>(),
                new FakeTimeProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero)),
                Substitute.For<ILogger<PipelineStageDispatchPlanner>>());
        }

        public Task DispatchAsync(Dictionary<string, string> variables) =>
            _planner.CreateTasksForNextStageAsync(
                RunId, _definition, variables, PendingSteps, null, [],
                Substitute.For<IPipelineChildRunLauncher>(), CancellationToken.None);
    }
}
