// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// A smoke step points a host operation at a public origin. Everything it refuses is a fail-closed
/// decision: an unvalidated origin would let a pipeline aim a request at an arbitrary address from
/// inside the runner's network, so the step must fail honestly rather than dispatch a task that
/// cannot succeed.
/// </summary>
public class PipelineHostOperationSmokeTaskTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();

    private PipelineHostOperationTaskFactory Build()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        return new PipelineHostOperationTaskFactory(
            _repo,
            Substitute.For<IPipelineGitService>(),
            _encryption,
            Substitute.For<ISecretMaskingService>(),
            configuration,
            Substitute.For<IAppDeployEnvProvider>(),
            Substitute.For<ILogger<PipelineHostOperationTaskFactory>>(),
            TimeProvider.System);
    }

    private static Server Runner() => new()
    {
        Id = 5,
        Name = "runner-1",
        OsType = OsType.Linux,
        OsDescription = "Ubuntu 24.04"
    };

    private static PipelineStepRun Step() => new()
    {
        Id = 9,
        StepName = "smoke",
        StageName = "verify",
        Status = TaskExecutionStatus.Pending
    };

    private ServerTask? TrackedTask()
    {
        var call = _repo.ReceivedCalls().FirstOrDefault(c => c.GetMethodInfo().Name == nameof(IPipelineRepository.TrackTask));
        return call?.GetArguments()[0] as ServerTask;
    }

    private async Task<PipelineStepRun> RunSmokeAsync(
        string? origin, Dictionary<string, string>? vars = null, int? timeout = null)
    {
        var step = Step();
        var definition = new PipelineStepDefinition
        {
            Name = "smoke",
            Origin = origin,
            TimeoutSeconds = timeout ?? 120
        };
        // The smoke path needs a bootstrap identity; a pipeline that declares it itself is the
        // ordinary case, and the derivation-from-configuration path is covered separately.
        var legVars = vars ?? new Dictionary<string, string>(StringComparer.Ordinal);
        legVars.TryAdd("AETHEUS_SMOKE_ADMIN_USER", "smoke-admin");
        legVars.TryAdd("AETHEUS_SMOKE_ADMIN_PASSWORD", "smoke-pwd");
        await Build().CreateSmokeTaskAsync(
            runId: 1, Runner(), step, definition, legVars, TestContext.Current.CancellationToken);
        return step;
    }

    [Fact]
    public async Task AMissingOrigin_FailsTheStepAndDispatchesNothing()
    {
        var step = await RunSmokeAsync(origin: null);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task AnEmptyOrigin_FailsTheStep()
    {
        var step = await RunSmokeAsync(origin: "   ");

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task AnOriginThatIsNotAValidTarget_FailsTheStep()
    {
        // Fail-closed: the runner must not be pointed at an arbitrary string from a YAML file.
        var step = await RunSmokeAsync(origin: "not a url at all");

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task AValidOrigin_DispatchesASmokeTaskOnTheLegRunner()
    {
        var step = await RunSmokeAsync(origin: "https://app.example.com");

        var task = TrackedTask();
        Assert.NotNull(task);
        Assert.Equal(OperationKind.PipelineSmoke, task!.Operation);
        Assert.Equal(5, task.ServerId);
        Assert.Equal(1, task.PipelineRunId);
        Assert.Equal(9, task.PipelineStepRunId);
        Assert.Equal(5, step.ServerId);
        Assert.NotEqual(TaskExecutionStatus.Failed, step.Status);
    }

    [Fact]
    public async Task ATrailingSlashOnTheOrigin_IsRemovedBeforeDispatch()
    {
        // The agent appends its own path; a doubled slash makes the smoke request miss.
        await RunSmokeAsync(origin: "https://app.example.com/");

        Assert.Equal("https://app.example.com", TrackedTask()!.Command);
    }

    [Fact]
    public async Task TheOriginIsSubstitutedFromTheLegVariables()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["HOST"] = "staging.example.com" };

        await RunSmokeAsync(origin: "https://$(HOST)", vars: vars);

        Assert.Equal("https://staging.example.com", TrackedTask()!.Command);
    }

    [Fact]
    public async Task TheStepTimeoutIsCarriedOntoTheDispatchedTask()
    {
        await RunSmokeAsync(origin: "https://app.example.com", timeout: 45);

        Assert.Equal(45, TrackedTask()!.TimeoutSeconds);
    }

    [Fact]
    public async Task TheTaskEnvironmentIsProtected_NeverStoredAsPlainJson()
    {
        // The smoke environment carries a bootstrap identity; storing it unprotected would put a
        // usable credential in the tasks table.
        await RunSmokeAsync(origin: "https://app.example.com");

        // Assert that protection was APPLIED, not that the stored string looks encrypted: the stub
        // here returns "enc:" + plaintext, so inspecting its output would only test the stub.
        Assert.NotNull(TrackedTask()!.EnvironmentVariables);
        _encryption.Received().EncryptValue(Arg.Is<string>(json =>
            json.Contains("AETHEUS_SMOKE_ADMIN_PASSWORD", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AValidOriginWithNoBootstrapIdentityAndNoConfiguration_FailsClosed()
    {
        // Neither declared by the pipeline nor derivable from configuration: the step must fail
        // rather than dispatch a smoke run that cannot authenticate.
        var step = Step();
        var definition = new PipelineStepDefinition { Name = "smoke", Origin = "https://app.example.com" };

        await Build().CreateSmokeTaskAsync(
            runId: 1, Runner(), step, definition,
            new Dictionary<string, string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    // --- blue-green dispatch ------------------------------------------------

    private async Task<PipelineStepRun> RunBlueGreenAsync(
        string type, Dictionary<string, string>? vars = null, PipelineStepDefinition? definition = null)
    {
        var step = Step();
        var legVars = vars ?? new Dictionary<string, string>(StringComparer.Ordinal);
        await Build().CreateBlueGreenTaskAsync(
            type, runId: 1, Runner(), step,
            definition ?? new PipelineStepDefinition { Name = "bg", Project = "toto", TimeoutSeconds = 300 },
            legVars, TestContext.Current.CancellationToken);
        return step;
    }

    [Theory]
    [InlineData("bluegreen-nonsense")]
    [InlineData("bluegreen")]
    [InlineData("")]
    public async Task AnUnknownBlueGreenStepType_FailsTheStepAndDispatchesNothing(string type)
    {
        // A typo in the YAML must not silently dispatch a task with OperationKind.None, which the
        // agent would not know how to run.
        var step = await RunBlueGreenAsync(type);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task BlueGreenUpWithoutABootstrapIdentity_FailsClosed()
    {
        // Only the step that STARTS the candidate colour needs the identity, because it has to be in
        // the container environment at start-up.
        var step = await RunBlueGreenAsync("bluegreen-up");

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Theory]
    [InlineData("bluegreen-switch")]
    [InlineData("bluegreen-commit")]
    [InlineData("bluegreen-rollback")]
    public async Task ABlueGreenStepWithAnIncompleteBindingFailsClosed(string type)
    {
        // These three need no bootstrap identity (they act on a colour that already carries it), so
        // they reach the binding stage. The definition here declares only a project name: the missing
        // state_dir alone is enough to refuse the dispatch, which is the fail-closed property that
        // matters - nothing partial reaches a compose invocation on the host.
        var step = await RunBlueGreenAsync(type);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    /// <summary>
    /// D-01: a rollback after a failure that came before the image tags were published still runs.
    /// Recette R2-092 (aetheus-demo #2534): it no longer writes a run warning about the names it could
    /// not forward. bluegreen-up refuses an undefined name, so such a rollback belongs to a run that
    /// never started a colour; the agent's rollback says there is nothing to undo.
    /// </summary>
    [Fact]
    public async Task R2092_ARollbackMissingPublishedTags_IsDispatchedWithoutARunWarning()
    {
        var step = await RunBlueGreenAsync(
            "bluegreen-rollback",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["APP_VERSION"] = "1.1.7" },
            MissingTagsDefinition());

        Assert.NotEqual(TaskExecutionStatus.Failed, step.Status);
        Assert.Equal(OperationKind.BlueGreenRollback, TrackedTask()!.Operation);
        await _repo.DidNotReceive().AppendRunWarningsAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>D-01 on the other steps acting on existing containers: a skipped name is still reported.</summary>
    [Fact]
    public async Task ACommitMissingPublishedTags_IsDispatchedAndNamesWhatItSkipped()
    {
        var step = await RunBlueGreenAsync(
            "bluegreen-commit",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["APP_VERSION"] = "1.1.7" },
            MissingTagsDefinition());

        Assert.NotEqual(TaskExecutionStatus.Failed, step.Status);
        Assert.NotNull(TrackedTask());
        await _repo.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Single().Contains("AETHEUS_BACK_IMAGE", StringComparison.Ordinal)
                && !warnings.Single().Contains("APP_VERSION", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private static PipelineStepDefinition MissingTagsDefinition() => new()
    {
        Name = "bg",
        Project = "toto",
        StateDir = "/var/lib/toto",
        EnvFile = "/var/lib/toto/.env",
        ComposeFiles = "deploy/compose/base.yml",
        Ports = "10029,10030,10031,10032",
        ReloadHelper = "/usr/local/lib/aetheus/apache-reload",
        UpstreamConf = "/etc/apache2/sites-available/toto.conf",
        ComposeEnv = "AETHEUS_BACK_IMAGE,APP_VERSION",
        TimeoutSeconds = 300
    };
}
