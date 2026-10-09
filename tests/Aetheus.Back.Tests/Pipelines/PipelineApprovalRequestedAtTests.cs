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
/// Production deployment was impossible and nothing said why. An approval raised for a stage on an
/// environment with RequireApproval was stored with RequestedAt at DateTime's default, because the
/// planner never set it and nothing else writes that column. The reconcile sweep expires a pending
/// approval once RequestedAt + ApprovalTimeoutMinutes is in the past, and year 1 plus a day always
/// is, so every production approval was auto-decided TimedOut on the first sweep - about a minute
/// after it was raised, long before a human could answer. aetheus-deploy-prod runs 2307 and 2313
/// both died exactly that way, one minute in, with no failed step and no log to explain it.
///
/// The expiry query itself was already covered, which is why this went unnoticed: those tests
/// construct the approval and set RequestedAt themselves, so they never exercised the one code path
/// that creates one for real.
/// </summary>
public class PipelineApprovalRequestedAtTests
{
    private const int RunId = 7311;

    [Fact]
    public async Task AnApprovalRaisedForAGatedEnvironment_IsStampedWithTheCurrentTime()
    {
        var now = new DateTimeOffset(2026, 9, 9, 14, 45, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var repo = Substitute.For<IPipelineRepository>();
        var finalizer = Substitute.For<IPipelineRunFinalizer>();
        var environmentChecks = Substitute.For<IPipelineEnvironmentCheckGuard>();
        var servers = Substitute.For<IPipelineDispatchServerResolver>();

        repo.HasActiveArtifactCollectionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetTerminalStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.HasAnyFailedStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.GetActiveStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        repo.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.GetApprovalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment
            {
                Id = 1,
                Name = "prod",
                RequireApproval = true,
                ApprovalTimeoutMinutes = 1440
            });
        repo.TryTransitionPipelineRunStatusAsync(
            Arg.Any<int>(), PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>())
            .Returns(true);
        environmentChecks.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        finalizer.IsRunActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        servers.CheckDeploymentPolicy(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Server>()).Returns((string?)null);
        servers.ResolveEffectiveStageTarget(Arg.Any<PipelineStageDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(callInfo => callInfo.Arg<PipelineStageDefinition>());

        var planner = new PipelineStageDispatchPlanner(
            repo, servers, environmentChecks, finalizer,
            Substitute.For<IPipelineSystemTaskFactory>(),
            Substitute.For<IPipelineStepTaskDispatcher>(),
            Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<IDomainEventDispatcher>(),
            time,
            Substitute.For<ILogger<PipelineStageDispatchPlanner>>());

        var stage = new PipelineStageDefinition { Name = "Restore candidate", Environment = "prod" };
        await planner.CreateTasksForNextStageAsync(
            RunId,
            new PipelineYamlDefinition { Name = "aetheus-deploy-prod", Stages = [stage] },
            [],
            [new PipelineStepRun { StageName = "Restore candidate", Status = TaskExecutionStatus.Pending }],
            null,
            [],
            Substitute.For<IPipelineChildRunLauncher>(),
            CancellationToken.None);

        // The assertion that matters is not "an approval was created" but that it carries a usable
        // request time. Default(DateTime) here is what made every production deployment expire.
        await repo.Received(1).AddApprovalAsync(
            Arg.Is<PipelineApproval>(a => a.RequestedAt == now.UtcDateTime),
            Arg.Any<CancellationToken>());
    }
}
