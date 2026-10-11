// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// D-02 through the real planner: the variables a dispatch pass hands to the next stage are the
/// declared values expanded against what completed steps published, not the launch-time text. The
/// stage stops at its approval gate, which is after output injection and before any runner is picked,
/// so the map the pass computed can be read back without dispatching anything.
/// </summary>
public class PipelineStageDispatchOutputExpansionTests
{
    private const int RunId = 7313;

    [Fact]
    public async Task ADispatchPass_ExpandsDeclaredValuesAgainstPublishedOutputs()
    {
        var planner = Harness(
        [
            new StepOutputProjection("Prepare", "prepare", """{"SOURCE_COMMIT":"abc123"}""")
        ]);
        var resolvedVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BG_BROWSER_IMAGE"] = "aetheus-browser-smoke:$(SOURCE_COMMIT)",
            ["CHANNEL"] = "$(RELEASE_CHANNEL:-stable)"
        };
        var stage = new PipelineStageDefinition { Name = "Evidence", Environment = "prod" };

        await planner.CreateTasksForNextStageAsync(
            RunId,
            new PipelineYamlDefinition { Name = "deploy", Stages = [stage] },
            resolvedVars,
            [new PipelineStepRun { StageName = "Evidence", Status = TaskExecutionStatus.Pending }],
            null,
            [],
            Substitute.For<IPipelineChildRunLauncher>(),
            TestContext.Current.CancellationToken);

        Assert.Equal("aetheus-browser-smoke:abc123", resolvedVars["BG_BROWSER_IMAGE"]);
        Assert.Equal("stable", resolvedVars["CHANNEL"]);
    }

    private static PipelineStageDispatchPlanner Harness(List<StepOutputProjection> outputs)
    {
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
        repo.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(outputs);
        repo.GetApprovalsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        repo.FindCandidateTargetServerIdsAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
            Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns([]);
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

        return new PipelineStageDispatchPlanner(
            repo, servers, environmentChecks, finalizer,
            Substitute.For<IPipelineSystemTaskFactory>(),
            Substitute.For<IPipelineStepTaskDispatcher>(),
            Substitute.For<IHubContext<PipelineHub>>(),
            Substitute.For<IDomainEventDispatcher>(),
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero)),
            Substitute.For<ILogger<PipelineStageDispatchPlanner>>());
    }
}
