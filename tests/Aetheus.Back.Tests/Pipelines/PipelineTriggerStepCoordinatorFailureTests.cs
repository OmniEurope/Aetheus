// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// A `type: trigger` step that cannot start its child must FAIL, out loud.
///
/// This came from a real wedge on the mirror: `publish-observability-packages` passed the package
/// version to its children as a variable, while each child declares `version` as a required
/// parameter. The launch threw "Parameter 'version' is required." from outside the one try/catch in
/// this method, so the exception unwound the entire scheduling pass. The run kept the status
/// Running with every step Pending - no step marked, no warning recorded - and the reconcile sweep
/// re-threw on it once a minute, for as long as the backend stayed up. A run that reports nothing
/// forever is worse than a failed one, because nobody is told to look at it.
/// </summary>
public sealed class PipelineTriggerStepCoordinatorFailureTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineGitService _git = Substitute.For<IPipelineGitService>();
    private readonly IPipelineRunPreparationService _preparations = Substitute.For<IPipelineRunPreparationService>();
    private readonly IPipelineRunParameterResolver _parameters = Substitute.For<IPipelineRunParameterResolver>();
    private readonly IPipelineCheckpointReuseService _checkpoints = Substitute.For<IPipelineCheckpointReuseService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IPipelineChildRunLauncher _launcher = Substitute.For<IPipelineChildRunLauncher>();
    private readonly PipelineTriggerStepCoordinator _sut;
    private readonly Pipeline _childPipeline;

    private const int ParentRunId = 1118;
    private const int ProjectId = 13;
    private static readonly HashSet<string> NoSecrets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The parent's secret keys: a vault entry and an alias BACK-022 classified with it.</summary>
    private static readonly HashSet<string> Secrets = new(StringComparer.OrdinalIgnoreCase) { "VAULT_DB_PASSWORD", "DB_ALIAS" };

    public PipelineTriggerStepCoordinatorFailureTests()
    {
        _sut = new PipelineTriggerStepCoordinator(
            _repo, _git, _preparations, _parameters, _checkpoints, _authz, _audit,
            new FakeTimeProvider(),
            NullLogger<PipelineTriggerStepCoordinator>.Instance);

        var parentPipeline = new Pipeline { Id = 77, Name = "publish-observability-packages", ProjectId = ProjectId };
        var parentRun = new PipelineRun { Id = ParentRunId, PipelineId = parentPipeline.Id, Pipeline = parentPipeline };
        var childPipeline = _childPipeline = new Pipeline
        {
            Id = 74,
            Name = "package-aetheus-telemetry",
            ProjectId = ProjectId,
            CreatedByUsername = "admin",
        };

        _repo.GetPipelineRunWithPipelineAsync(ParentRunId, Arg.Any<CancellationToken>()).Returns(parentRun);
        _repo.GetPipelineProjectIdAsync(parentPipeline, Arg.Any<CancellationToken>()).Returns(ProjectId);
        _repo.FindPipelineByNameAndProjectAsync("package-aetheus-telemetry", ProjectId, Arg.Any<CancellationToken>())
            .Returns(childPipeline);
        _preparations.PrepareRunAsync(
                childPipeline.Id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunPreparation
            {
                PipelineId = childPipeline.Id,
                YamlSnapshot = "name: package-aetheus-telemetry",
                TargetServerIds = [],
            });
        _parameters.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<PipelineRunPreparation>(0));
        _authz.HasPermissionAsync(
                Arg.Any<string>(), Arg.Any<ResourceType>(), Arg.Any<int>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _checkpoints.TryReuseCheckpointAsync(
                Arg.Any<PipelineRun>(), Arg.Any<Pipeline>(), Arg.Any<string>(), Arg.Any<PipelineRunPreparation>(),
                Arg.Any<Dictionary<string, string>>(), Arg.Any<PipelineStepRun>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
    }

    private static PipelineStepRun Step() => new()
    {
        Id = 9510,
        PipelineRunId = ParentRunId,
        StageName = "TelemetryCandidate",
        StepName = "Validate Telemetry candidate",
        Status = TaskExecutionStatus.Pending,
    };

    private static PipelineStepDefinition Definition(string pipeline = "package-aetheus-telemetry") => new()
    {
        Name = "Validate Telemetry candidate",
        Type = "trigger",
        Pipeline = pipeline,
    };

    /// <summary>
    /// The exact wedge: the child refuses the launch because a required parameter was not supplied.
    /// The step must end Failed with the child's own words, and the run must carry a warning, so the
    /// pipeline reports the refusal instead of silently spinning.
    /// </summary>
    [Fact]
    public async Task ARefusedChildLaunch_FailsTheStepInsteadOfWedgingTheRun()
    {
        _launcher.TriggerPreparedRunAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Throws(new BadRequestException("Parameter 'version' is required."));

        var step = Step();

        // Not Assert.ThrowsAsync's opposite by accident: an unhandled exception here unwinds the
        // whole scheduling pass, which is precisely the wedge this test exists to prevent.
        var exception = await Record.ExceptionAsync(() => _sut.CreateTriggerStepAsync(
            ParentRunId, step, Definition(), [], NoSecrets, _launcher, CancellationToken.None));
        Assert.Null(exception);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        Assert.Contains("Parameter 'version' is required.", step.FailureReason);

        await _repo.Received(1).AppendRunWarningsAsync(
            ParentRunId,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(m => m.Contains("Parameter 'version' is required."))),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The success path is untouched by the guard: a child that starts leaves the step Running and
    /// linked to it, which is what lets the completion handler settle it later.
    /// </summary>
    [Fact]
    public async Task AnAcceptedChildLaunch_LeavesTheStepRunningAndLinked()
    {
        _launcher.TriggerPreparedRunAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new PipelineRunDto { Id = 1119 });

        var step = Step();

        await _sut.CreateTriggerStepAsync(
            ParentRunId, step, Definition(), [], NoSecrets, _launcher, CancellationToken.None);

        Assert.Equal(TaskExecutionStatus.Running, step.Status);
        Assert.Equal(1119, step.TriggeredRunId);
    }

    /// <summary>
    /// BACK-021: the parent passes runner=B to a child whose stage reads agent: ${{ parameters.runner }}.
    /// Prepared without parameters, that selector matched nothing and the owner was "authorized" on an
    /// empty set; the child then ran on B. The owner's rights are checked on the targets the parameters
    /// produce, and the child starts from that same preparation.
    /// </summary>
    [Fact]
    public async Task ParameterChosenTarget_OwnerWithoutServerAdmin_FailsTheStepBeforeLaunch()
    {
        var resolved = new PipelineRunPreparation
        {
            PipelineId = 74,
            YamlSnapshot = "name: package-aetheus-telemetry",
            TargetServerIds = [42],
        };
        _parameters.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(),
                Arg.Is<IReadOnlyDictionary<string, string>?>(p => p != null && p["runner"] == "b"),
                Arg.Any<CancellationToken>())
            .Returns(resolved);
        _authz.HasPermissionAsync("admin", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);
        var definition = Definition();
        definition.Parameters["runner"] = "b";
        var step = Step();

        await _sut.CreateTriggerStepAsync(ParentRunId, step, definition, [], NoSecrets, _launcher, CancellationToken.None);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        Assert.Contains("lacks Server.Admin", step.FailureReason);
        await _launcher.DidNotReceive().TriggerPreparedRunAsync(
            Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ParameterChosenTarget_OwnerWithServerAdmin_LaunchesTheAuthorizedPreparation()
    {
        var resolved = new PipelineRunPreparation
        {
            PipelineId = 74,
            YamlSnapshot = "name: package-aetheus-telemetry",
            TargetServerIds = [42],
        };
        _parameters.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(resolved);
        _launcher.TriggerPreparedRunAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new PipelineRunDto { Id = 1119 });
        var definition = Definition();
        definition.Parameters["runner"] = "b";

        await _sut.CreateTriggerStepAsync(ParentRunId, Step(), definition, [], NoSecrets, _launcher, CancellationToken.None);

        await _authz.Received().HasPermissionAsync("admin", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>());
        await _launcher.Received(1).TriggerPreparedRunAsync(
            Arg.Is<PipelineRunPreparation>(prepared => ReferenceEquals(prepared, resolved)),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RunToEndParent_StartsItsChildInRunToEndToo()
    {
        var parent = await _repo.GetPipelineRunWithPipelineAsync(ParentRunId, TestContext.Current.CancellationToken);
        parent!.RunToEnd = true;
        _launcher.TriggerPreparedRunAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new PipelineRunDto { Id = 1119 });

        await _sut.CreateTriggerStepAsync(ParentRunId, Step(), Definition(), [], NoSecrets, _launcher, CancellationToken.None);

        await _launcher.Received(1).TriggerPreparedRunAsync(
            Arg.Is<PipelineRunPreparation>(prepared => prepared.RunToEnd && prepared.PipelineId == 74),
            Arg.Any<Dictionary<string, string>>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string>());
    }

    /// <summary>
    /// Neighbour of BACK-022: the child run persists the variables and parameters it is handed
    /// (AdditionalVariablesJson, ParametersJson). A secret, or an alias of one, substituted into them
    /// was written there in clear. It travels as its reference instead, which the child resolves from
    /// its own vault and classifies as a secret (or refuses at launch when it cannot provide it).
    /// </summary>
    [Fact]
    public async Task ASecretPassedToTheChild_TravelsAsItsReference_NeverAsItsValue()
    {
        Dictionary<string, string>? childVariables = null;
        Dictionary<string, string>? childParameters = null;
        _launcher.TriggerPreparedRunAsync(
                Arg.Any<PipelineRunPreparation>(),
                Arg.Do<Dictionary<string, string>>(variables => childVariables = variables),
                Arg.Do<Dictionary<string, string>?>(parameters => childParameters = parameters),
                Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new PipelineRunDto { Id = 1119 });
        var definition = Definition();
        definition.Variables["DB_PASSWORD"] = "$(VAULT_DB_PASSWORD)";
        definition.Variables["CONNECTION"] = "Host=db;Password=$(DB_ALIAS)";
        definition.Variables["PLAIN"] = "$(PUBLIC_VALUE)";
        definition.Parameters["token"] = "$(VAULT_DB_PASSWORD:-none)";
        var legVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["VAULT_DB_PASSWORD"] = "s3cr3t-value",
            ["DB_ALIAS"] = "s3cr3t-value",
            ["PUBLIC_VALUE"] = "visible",
        };

        await _sut.CreateTriggerStepAsync(
            ParentRunId, Step(), definition, legVariables, Secrets, _launcher, CancellationToken.None);

        Assert.NotNull(childVariables);
        Assert.NotNull(childParameters);
        Assert.DoesNotContain(childVariables.Values, value => value.Contains("s3cr3t-value", StringComparison.Ordinal));
        Assert.DoesNotContain(childParameters.Values, value => value.Contains("s3cr3t-value", StringComparison.Ordinal));
        Assert.Equal("$(VAULT_DB_PASSWORD)", childVariables["DB_PASSWORD"]);
        Assert.Equal("Host=db;Password=$(DB_ALIAS)", childVariables["CONNECTION"]);
        Assert.Equal("$(VAULT_DB_PASSWORD:-none)", childParameters["token"]);
        Assert.Equal("visible", childVariables["PLAIN"]);
    }

    /// <summary>A secret passed under its own name would reach the child as the literal reference and
    /// replace the child's own vault value with it: it is not forwarded, the child reads its vault.</summary>
    [Fact]
    public async Task ASecretPassedUnderItsOwnName_IsLeftToTheChildsVault()
    {
        Dictionary<string, string>? childVariables = null;
        _launcher.TriggerPreparedRunAsync(
                Arg.Any<PipelineRunPreparation>(),
                Arg.Do<Dictionary<string, string>>(variables => childVariables = variables),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new PipelineRunDto { Id = 1119 });
        var definition = Definition();
        definition.Variables["VAULT_DB_PASSWORD"] = "$(VAULT_DB_PASSWORD)";
        var legVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["VAULT_DB_PASSWORD"] = "s3cr3t-value",
        };

        await _sut.CreateTriggerStepAsync(
            ParentRunId, Step(), definition, legVariables, Secrets, _launcher, CancellationToken.None);

        Assert.NotNull(childVariables);
        Assert.False(childVariables.ContainsKey("VAULT_DB_PASSWORD"));
    }

    [Fact]
    public async Task ChainedRun_ParameterChosenTarget_OwnerWithoutServerAdmin_IsNotLaunched()
    {
        var childPipeline = new Pipeline { Id = 74, Name = "package-aetheus-telemetry", CreatedByUsername = "admin" };
        _repo.FindPipelineAsync(74, Arg.Any<CancellationToken>()).Returns(childPipeline);
        _parameters.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunPreparation { PipelineId = 74, YamlSnapshot = "name: x", TargetServerIds = [42] });
        _authz.HasPermissionAsync("admin", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var child = await _sut.TriggerChainedRunAsync(74, null, _launcher, CancellationToken.None);

        Assert.Null(child);
        await _launcher.DidNotReceive().TriggerPreparedRunAsync(
            Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    /// <summary>Owners lacking Server.Admin are refused and audited; transferring ownership to an
    /// active administrator permits the same chained run.</summary>
    [Fact]
    public async Task InactiveOwner_IsRefused_AndTheSameStepLaunchesAfterTheOwnerChanges()
    {
        var resolved = new PipelineRunPreparation
        {
            PipelineId = 74,
            YamlSnapshot = "name: package-aetheus-telemetry",
            TargetServerIds = [42],
        };
        _parameters.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(resolved);
        _authz.HasPermissionAsync("admin", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);
        _authz.HasPermissionAsync("ops", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _launcher.TriggerPreparedRunAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new PipelineRunDto { Id = 1120 });

        var refused = Step();
        await _sut.CreateTriggerStepAsync(ParentRunId, refused, Definition(), [], NoSecrets, _launcher, CancellationToken.None);

        Assert.Equal(TaskExecutionStatus.Failed, refused.Status);
        Assert.Contains("owner 'admin'", refused.FailureReason);
        Assert.Contains("lacks Server.Admin", refused.FailureReason);
        await _audit.Received(1).LogAsync(
            "BlockedUnauthorizedChainedRun", "Pipeline", 74, Arg.Any<string?>(), Arg.Any<CancellationToken>());

        _childPipeline.CreatedByUsername = "ops";
        var launched = Step();
        await _sut.CreateTriggerStepAsync(ParentRunId, launched, Definition(), [], NoSecrets, _launcher, CancellationToken.None);

        Assert.Equal(TaskExecutionStatus.Running, launched.Status);
        Assert.Equal(1120, launched.TriggeredRunId);
    }

    [Fact]
    public async Task AStepWithoutAPipelineName_FailsWithoutReadingTheRun()
    {
        var step = Step();
        var definition = Definition("  ");

        await _sut.CreateTriggerStepAsync(ParentRunId, step, definition, [], NoSecrets, _launcher, CancellationToken.None);

        await AssertFailed(step, "missing 'pipeline' name to trigger.");
        await _repo.DidNotReceive().GetPipelineRunWithPipelineAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AParentOutsideAProject_FailsTheStep()
    {
        _repo.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        var step = Step();

        await _sut.CreateTriggerStepAsync(ParentRunId, step, Definition(), [], NoSecrets, _launcher, CancellationToken.None);

        await AssertFailed(step, "trigger is only supported for project-owned pipelines.");
    }

    [Fact]
    public async Task AnUnknownTargetWithoutAnImmutableRevision_FailsTheStep()
    {
        var step = Step();
        var definition = Definition("does-not-exist");

        await _sut.CreateTriggerStepAsync(ParentRunId, step, definition, [], NoSecrets, _launcher, CancellationToken.None);

        await AssertFailed(step, "pipeline 'does-not-exist' not found in the project or at the immutable run revision.");
    }

    [Fact]
    public async Task ATargetAlreadyInTheChain_IsRefusedAsACycle()
    {
        _repo.FindPipelineByNameAndProjectAsync("package-aetheus-telemetry", ProjectId, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 77, Name = "package-aetheus-telemetry", ProjectId = ProjectId });
        var step = Step();

        await _sut.CreateTriggerStepAsync(ParentRunId, step, Definition(), [], NoSecrets, _launcher, CancellationToken.None);

        await AssertFailed(step, "cycle refused: 'package-aetheus-telemetry' is already in the orchestration chain.");
    }

    [Fact]
    public async Task ATargetWhoseConfigurationIsRefused_FailsWithTheRefusal()
    {
        _preparations.PrepareRunAsync(
                74, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new BadRequestException("stage 'build' has no steps"));
        var step = Step();

        await _sut.CreateTriggerStepAsync(ParentRunId, step, Definition(), [], NoSecrets, _launcher, CancellationToken.None);

        await AssertFailed(step, "pipeline 'package-aetheus-telemetry' configuration refused: stage 'build' has no steps");
    }

    [Fact]
    public async Task ATargetThatCannotBePrepared_IsRefusedLikeAnUnauthorizedOne()
    {
        _preparations.PrepareRunAsync(
                74, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((PipelineRunPreparation?)null);
        _childPipeline.CreatedByUsername = null;
        var step = Step();

        await _sut.CreateTriggerStepAsync(ParentRunId, step, Definition(), [], NoSecrets, _launcher, CancellationToken.None);

        await AssertFailed(step, "owner '(none)' of pipeline 'package-aetheus-telemetry' lacks Server.Admin on its target servers.");
    }

    private async Task AssertFailed(PipelineStepRun step, string reason)
    {
        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        Assert.Equal(reason, step.FailureReason);
        await _repo.Received(1).AppendRunWarningsAsync(
            ParentRunId,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Single() == $"Trigger step 'Validate Telemetry candidate': {reason}"),
            Arg.Any<CancellationToken>());
        await _launcher.DidNotReceive().TriggerPreparedRunAsync(
            Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
    }
}
