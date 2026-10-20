// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// What the control plane hands the agent for a <c>type: aetheus</c> step. The real task builder is
/// used, so the assertions read the task as the agent will receive it: kind, target, timeout and the
/// protected environment carrying the encoded <c>with:</c> values.
/// </summary>
public sealed class PipelineFunctionTaskFactoryTests
{
    private const string EncryptedPrefix = "enc:";

    private static readonly PipelineFunctionDescriptor DeploymentFunction = new(
        "rotate-environment-keys",
        PipelineFunctionAccess.Deployment,
        "Test-only deployment function.",
        [new PipelineFunctionParameter("environment", Required: true, "Target environment.")],
        []);

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly List<ServerTask> _tracked = [];

    public PipelineFunctionTaskFactoryTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => EncryptedPrefix + call.Arg<string>());
        _repo.When(repo => repo.TrackTask(Arg.Any<ServerTask>()))
            .Do(call => _tracked.Add(call.Arg<ServerTask>()));
    }

    private PipelineFunctionTaskFactory Factory() => new(
        new PipelineStepTaskBuilder(_repo, _encryption, NullLogger<PipelineStepTaskBuilder>.Instance),
        TimeProvider.System);

    private static PipelineStepDefinition Step(string? function, Dictionary<string, string>? with = null) => new()
    {
        Name = "Detect languages",
        Type = PipelineFunctionCatalog.StepType,
        Function = function,
        With = with ?? []
    };

    private static Dictionary<string, string> LegVars() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["WORKSPACE"] = "/srv/run/42",
        ["SOURCE_DIR"] = "src"
    };

    private static PipelineStepRun StepRun() => new() { Id = 9, StepName = "Detect languages" };

    private string? Create(PipelineStepDefinition step) => Factory().CreateFunctionTask(
        42, new Server { Id = 1, Name = "runner" }, StepRun(), step, LegVars(),
        new PipelineStageDefinition { Name = "build" });

    private string? CreateFor(PipelineFunctionDescriptor function, PipelineStepDefinition step) =>
        Factory().CreateTaskFor(
            function, 42, new Server { Id = 1, Name = "runner" }, StepRun(), step, LegVars(),
            new PipelineStageDefinition { Name = "deploy" });

    private Dictionary<string, string> Environment(ServerTask task)
    {
        Assert.StartsWith(EncryptedPrefix, task.EnvironmentVariables, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
            task.EnvironmentVariables[EncryptedPrefix.Length..])!;
    }

    private static IReadOnlyDictionary<string, string> Parameters(Dictionary<string, string> environment)
    {
        Assert.True(PipelineFunctionVariables.TryParseParameters(
            environment[PipelineFunctionVariables.ParametersJson], out var parameters));
        return parameters;
    }

    [Fact]
    public void ABuildFunctionBecomesAPipelineFunctionTaskTargetingItsName()
    {
        Assert.Null(Create(Step("detect-project-languages")));

        var task = Assert.Single(_tracked);
        Assert.Equal(OperationKind.PipelineFunction, task.Operation);
        Assert.Equal("detect-project-languages", task.Command);
        Assert.Equal(42, task.PipelineRunId);
        Assert.Equal(9, task.PipelineStepRunId);
    }

    [Fact]
    public void ADeploymentFunctionBecomesAPipelineDeploymentFunctionTask()
    {
        Assert.Null(CreateFor(DeploymentFunction, Step(DeploymentFunction.Name, new() { ["environment"] = "prod" })));

        var task = Assert.Single(_tracked);
        Assert.Equal(OperationKind.PipelineDeploymentFunction, task.Operation);
        Assert.Equal("rotate-environment-keys", task.Command);
    }


    [Theory]
    [InlineData("ensure-tls-certificates", "hosts", "app.example.org", OperationKind.PipelineDeploymentFunction)]
    [InlineData("grant-acme-challenge-traversal", "webroot", "/var/www/acme", OperationKind.PipelineDeploymentFunction)]
    [InlineData("bluegreen-host-reset", "compose_project", "app-nightly", OperationKind.PipelineDeploymentFunction)]
    [InlineData("retain-docker-images", "repositories", "app-back app-front", OperationKind.PipelineDeploymentFunction)]
    [InlineData("prune-qa-buildx-cache", "builder", "qa-builder", OperationKind.PipelineFunction)]
    [InlineData("prune-stale-qa-compose", "project_prefix", "app-qa", OperationKind.PipelineFunction)]
    public void DeploymentEnvironmentFunctions_TravelAsTheTaskKindOfTheirAccess(
        string function, string parameter, string value, OperationKind kind)
    {
        Assert.Null(Create(Step(function, new() { [parameter] = value })));

        var task = Assert.Single(_tracked);
        Assert.Equal(kind, task.Operation);
        Assert.Equal(function, task.Command);
        Assert.Equal(value, Parameters(Environment(task))[parameter]);
    }
    [Fact]
    public void TheTimeoutDefaultsToThreeHundredSecondsAndFollowsTheStep()
    {
        Create(Step("detect-project-languages"));
        Create(Step("detect-project-languages") with { TimeoutSeconds = 45 });

        Assert.Equal([300, 45], _tracked.Select(task => task.TimeoutSeconds));
    }

    [Fact]
    public void TheWithValuesAreSubstitutedAndTravelEncodedInTheProtectedEnvironment()
    {
        Create(Step("detect-project-languages", new() { ["root"] = "$(SOURCE_DIR)/app" }));

        var environment = Environment(Assert.Single(_tracked));
        Assert.Equal(new Dictionary<string, string> { ["root"] = "src/app" }, Parameters(environment));
        Assert.Equal("42", environment["AETHEUS_RUN_ID"]);
        Assert.Equal("build", environment["AETHEUS_STAGE_NAME"]);
        Assert.Equal("/srv/run/42", environment["WORKSPACE"]);
    }

    [Fact]
    public void AStepWithoutParametersSendsAnEmptyMapRatherThanNone()
    {
        Create(Step("detect-project-languages"));

        var environment = Environment(Assert.Single(_tracked));
        Assert.Equal("{}", environment[PipelineFunctionVariables.ParametersJson]);
        Assert.Empty(Parameters(environment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("detect-project-language")]
    [InlineData("Detect-Project-Languages")]
    public void AnUnknownFunctionIsRefusedAndNoTaskIsCreated(string? function)
    {
        var error = Create(Step(function));

        Assert.Contains("is not a known aetheus function", error!, StringComparison.Ordinal);
        Assert.Empty(_tracked);
    }

    [Fact]
    public void AContainerIsolatedStageHandsItsContainerToTheTask()
    {
        const string image = "alpine:3.22@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var stage = new PipelineStageDefinition
        {
            Name = "quality",
            Isolation = new PipelineIsolationDefinition
            {
                Mode = PipelineIsolationDefinition.ModeContainer,
                Image = image,
                Shell = "sh",
                Network = "none",
                Memory = "2g"
            }
        };

        Assert.Null(Factory().CreateFunctionTask(
            42, new Server { Id = 1, Name = "runner" }, StepRun(), Step("detect-project-languages"), LegVars(), stage));

        var task = Assert.Single(_tracked);
        Assert.Equal(OperationKind.PipelineFunction, task.Operation);
        Assert.Equal(ExecutorType.Container, task.Executor);
        Assert.Equal(image, task.ContainerImage);
        Assert.Equal("sh", task.ContainerShell);
        Assert.Equal("none", task.ContainerNetwork);
        Assert.Equal("2g", task.ContainerMemory);
    }

    [Fact]
    public void AProcessStageKeepsTheTaskOnTheHost()
    {
        Assert.Null(Create(Step("detect-project-languages")));

        var task = Assert.Single(_tracked);
        Assert.Null(task.ContainerImage);
        Assert.NotEqual(ExecutorType.Container, task.Executor);
    }

    [Fact]
    public void AnUndeclaredParameterIsRefused()
    {
        var error = Create(Step("detect-project-languages", new() { ["depth"] = "3" }));

        Assert.Contains("has no parameter 'depth'", error!, StringComparison.Ordinal);
        Assert.Empty(_tracked);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "")]
    [InlineData(true, "  ")]
    public void AMissingRequiredParameterIsRefused(bool present, string? value)
    {
        var with = present ? new Dictionary<string, string> { ["environment"] = value! } : [];

        var error = CreateFor(DeploymentFunction, Step(DeploymentFunction.Name, with));

        Assert.Contains("needs the parameter 'environment'", error!, StringComparison.Ordinal);
        Assert.Empty(_tracked);
    }

    // Mirror CI run 1272: the step producing CI_BUILDX_BUILDER had failed, so the prune function was
    // handed the literal text "$(CI_BUILDX_BUILDER)" as its builder name and ran on it.
    [Fact]
    public void AnUnresolvedReferenceInAContinuableStepFailsThatStepWithoutATask()
    {
        var stepRun = StepRun();
        stepRun.ContinueOnError = true;

        var error = Factory().CreateFunctionTask(
            42, new Server { Id = 1, Name = "runner" }, stepRun,
            Step("prune-qa-buildx-cache", new() { ["builder"] = "$(CI_BUILDX_BUILDER)" }), LegVars(),
            new PipelineStageDefinition { Name = "build" });

        Assert.Null(error);
        Assert.Empty(_tracked);
        Assert.Equal(TaskExecutionStatus.Failed, stepRun.Status);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, stepRun.FailureCode);
        Assert.Contains("'builder' references $(CI_BUILDX_BUILDER)", stepRun.FailureReason!, StringComparison.Ordinal);
        Assert.NotNull(stepRun.CompletedAt);
    }

    [Fact]
    public void AnUnresolvedReferenceInAStepThatMayNotFailIsRefusedWithoutATask()
    {
        var error = Create(Step("prune-qa-buildx-cache", new() { ["builder"] = "$(CI_BUILDX_BUILDER)" }));

        Assert.Contains("'builder' references $(CI_BUILDX_BUILDER)", error!, StringComparison.Ordinal);
        Assert.Empty(_tracked);
    }

    [Fact]
    public void AReferenceWithADefaultIsResolvedAndNotRefused()
    {
        Assert.Null(Create(Step("prune-qa-buildx-cache", new() { ["builder"] = "$(CI_BUILDX_BUILDER:-qa-builder)" })));

        Assert.Equal("qa-builder", Parameters(Environment(Assert.Single(_tracked)))["builder"]);
    }
}
