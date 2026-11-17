// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-25: the readiness does not ask to create a key that already exists in a vault of the project (it says the
/// pipeline does not declare it), and does not ask for the environment sample of a step that will not run on the
/// launch's variables (a blue-green only step, on a replace deployment).</summary>
public sealed class PipelineUndefinedVariablesExistingKeyTests
{
    private const int ProjectId = 19;
    private const int PipelineId = 176;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Sample = "DB_PASSWORD=#{DB_PASSWORD}#\nJWT_KEY=#{JWT_KEY}#\n";

    // The shape of application-release-fast: the sample is rendered only by a blue-green step.
    private static string ReleaseFast(string strategy, string condition) => $$"""
        name: w1-release-fast
        variables:
          DEPLOY_STRATEGY: "{{strategy}}"
          DEPLOY_ENV_SAMPLE: ".pipeline/configs/env/production.env"
        stages:
          - name: Prepare host
            condition: "{{condition}}"
            steps:
              - name: Render the environment file
                env_template: "$(DEPLOY_ENV_SAMPLE)"
                shell: echo render
          - name: FastRelease
            steps:
              - name: Run
                shell: echo release
        """;

    private const string BlueGreenOnly = "eq(variables['DEPLOY_STRATEGY'], 'bluegreen')";

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IProjectConfigRepositoryService _config = Substitute.For<IProjectConfigRepositoryService>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();

    public PipelineUndefinedVariablesExistingKeyTests()
    {
        _config.ReadAsync(ProjectId, ".pipeline/configs/env/production.env", null, null, Arg.Any<CancellationToken>()).Returns(Sample);
        _pipelines.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(
            [new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "w1", DefaultBranch = "main", IsEmpty = false }]);
        _requirements.FindKeyHoldersAsync(Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PipelineKeyHolder>());
    }

    private async Task<PipelineSetupReadinessCheckDto?> UndefinedCheckAsync(string yaml)
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([(PipelineId, "w1-release-fast", yaml)]);
        var sut = new PipelineSetupReadinessService(
            Substitute.For<IPipelineTemplateService>(), _pipelines, _git, Substitute.For<IGitLightRepository>(),
            Substitute.For<IPipelineTemplateResolver>(), _requirements, _config,
            ReadinessVariableResolver.Create(), Substitute.For<IPipelineSetupServerRepository>(), NullLogger<PipelineSetupReadinessService>.Instance);
        var readiness = await sut.CheckProjectAsync(ProjectId, pipelineId: null, organizationId: 3, Ct);
        return readiness.Checks.SingleOrDefault(check => check.Kind == PipelineSetupReadinessKind.UndefinedVariables);
    }

    [Fact]
    public async Task OnAReplaceDeployment_TheSampleOfABlueGreenOnlyStep_AsksForNothing()
    {
        var check = await UndefinedCheckAsync(ReleaseFast("replace", BlueGreenOnly));

        Assert.Null(check);
    }

    private async Task<PipelineSetupReadinessCheckDto?> EmptyValuesCheckAsync(string yaml)
    {
        _requirements.FindEmptyValuesAsync(
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns([new PipelineEmptyValue(PipelineRequirementsChecker.LibraryKind, "DB_PASSWORD", 5, "w1.prod")]);
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([(PipelineId, "w1-release-fast", yaml)]);
        var sut = new PipelineSetupReadinessService(
            Substitute.For<IPipelineTemplateService>(), _pipelines, _git, Substitute.For<IGitLightRepository>(),
            Substitute.For<IPipelineTemplateResolver>(), _requirements, _config,
            ReadinessVariableResolver.Create(), Substitute.For<IPipelineSetupServerRepository>(), NullLogger<PipelineSetupReadinessService>.Instance);
        var readiness = await sut.CheckProjectAsync(ProjectId, pipelineId: null, organizationId: 3, Ct);
        return readiness.Checks.SingleOrDefault(check => check.Kind == PipelineSetupReadinessKind.EmptyLibraryValues);
    }

    private static string WithLibrary(string yaml) =>
        yaml.Replace("stages:", "variable_libraries:\n  - w1.prod\nstages:", StringComparison.Ordinal);

    [Fact]
    public async Task OnAReplaceDeployment_AnEmptyValueReadOnlyByTheSampleOfABlueGreenOnlyStep_IsNotToFill()
    {
        Assert.Null(await EmptyValuesCheckAsync(WithLibrary(ReleaseFast("replace", BlueGreenOnly))));
    }

    [Fact]
    public async Task OnABlueGreenDeployment_TheSameEmptyValue_IsStillToFill()
    {
        var check = await EmptyValuesCheckAsync(WithLibrary(ReleaseFast("bluegreen", BlueGreenOnly)));

        Assert.NotNull(check);
        Assert.Equal(["DB_PASSWORD"], check.Items);
    }

    [Fact]
    public async Task AnEmptyValue_ReadByAStepWhoseConditionCannotBeDecided_IsStillToFill()
    {
        Assert.NotNull(await EmptyValuesCheckAsync(WithLibrary(ReleaseFast("replace", "failed()"))));
    }

    [Fact]
    public async Task OnABlueGreenDeployment_TheSameSample_StillReportsItsUndefinedVariables()
    {
        var check = await UndefinedCheckAsync(ReleaseFast("bluegreen", BlueGreenOnly));

        Assert.NotNull(check);
        Assert.Equal(["DB_PASSWORD", "JWT_KEY"], check.Items);
    }

    [Fact]
    public async Task ACondition_ThatCannotBeDecided_KeepsTheWarning()
    {
        // A value the launch does not hold, and a status function: nothing proves the step is skipped.
        Assert.NotNull(await UndefinedCheckAsync(ReleaseFast("replace", "eq(variables['NOT_KNOWN_AT_LAUNCH'], 'x')")));
        Assert.NotNull(await UndefinedCheckAsync(ReleaseFast("replace", "failed()")));
    }

    [Fact]
    public async Task AKeyAVaultOfTheProjectHolds_IsSaidToExistButNotBeDeclared_NotToBeCreated()
    {
        _requirements.FindKeyHoldersAsync(Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns([new PipelineKeyHolder("JWT_KEY", PipelineSetupReadinessContainerKind.Vault, 9, "w1.prod")]);

        var check = await UndefinedCheckAsync(ReleaseFast("bluegreen", BlueGreenOnly));

        Assert.NotNull(check);
        var rows = check.UndefinedVariables.ToDictionary(row => row.Name);
        var jwt = rows["JWT_KEY"];
        Assert.Equal((9, "w1.prod", PipelineSetupReadinessContainerKind.Vault), (jwt.ExistingHolderId, jwt.ExistingHolderName, jwt.ExistingHolderKind));
        Assert.Equal((PipelineId, "w1-release-fast"), (jwt.PipelineId, jwt.PipelineName));
        // A key that exists nowhere stays reported as to create, with the pipeline to open all the same.
        var password = rows["DB_PASSWORD"];
        Assert.Null(password.ExistingHolderId);
        Assert.Equal(string.Empty, password.ExistingHolderName);
        Assert.Equal(PipelineId, password.PipelineId);
    }

    [Fact]
    public async Task AHolderThePipelineAlreadyDeclares_IsNotSaidToBeUndeclared()
    {
        _requirements.FindKeyHoldersAsync(Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns([new PipelineKeyHolder("JWT_KEY", PipelineSetupReadinessContainerKind.Vault, 9, "w1.prod")]);
        var declared = ReleaseFast("bluegreen", BlueGreenOnly).Replace("stages:", "vaults:\n  - w1.prod\nstages:", StringComparison.Ordinal);

        var check = await UndefinedCheckAsync(declared);

        Assert.NotNull(check);
        Assert.Null(check.UndefinedVariables.Single(row => row.Name == "JWT_KEY").ExistingHolderId);
    }
}
