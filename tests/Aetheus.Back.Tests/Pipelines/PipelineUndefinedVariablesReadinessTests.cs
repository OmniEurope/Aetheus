// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-9: the variables a pipeline reads that nothing defines are named before a run, with the run's own checks
/// (the unresolved-variable guard and the environment sample's placeholders), not at the first stage that needs them.</summary>
public sealed class PipelineUndefinedVariablesReadinessTests
{
    private const int ProjectId = 7;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string DeployYaml = """
        name: w7-deploy-prod
        parameters:
          - name: targetColour
            type: string
        variables:
          DEPLOY_ENV_SAMPLE: ".pipeline/configs/env/production.env"
        stages:
          - name: Deploy
            steps:
              - name: Render
                env_template: "$(DEPLOY_ENV_SAMPLE)"
                shell: echo $(API_HOST) $(targetColour) $(BUILD_BUILDID) $(SECRET_FROM_VAULT)
        """;

    private const string Sample = "APPNAME=#{APPNAME}#\nDB_USER=#{DB_USER}#\nDEPLOY_ENV=#{DEPLOY_ENV}#\n";

    private readonly IPipelineTemplateService _templates = Substitute.For<IPipelineTemplateService>();
    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IProjectConfigRepositoryService _config = Substitute.For<IProjectConfigRepositoryService>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();

    private const string SecretsYaml = """
        name: w7-deploy-prod
        variable_libraries:
          - w7.share
          - w7.prod
        vaults:
          - w7.vault
        stages:
          - name: Deploy
            steps:
              - name: Run
                shell: echo $(API_HOST) $(DB_PASSWORD) $(JWT_KEY) $(PORT_FRONT)
        """;

    private async Task<PipelineSetupReadinessCheckDto> UndefinedCheckAsync(string yaml)
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([(21, "w7-deploy-prod", yaml)]);
        var readiness = await Sut().CheckProjectAsync(ProjectId, pipelineId: null, organizationId: 3, Ct);
        return readiness.Checks.Single(check => check.Kind == PipelineSetupReadinessKind.UndefinedVariables);
    }

    private PipelineSetupReadinessService Sut(params string[] definedByALibraryOrVault) => new(
        _templates, _pipelines, _git, Substitute.For<IGitLightRepository>(),
        Substitute.For<IPipelineTemplateResolver>(), _requirements, _config,
        ReadinessVariableResolver.Create(definedByALibraryOrVault),
        Substitute.For<IPipelineSetupServerRepository>(), NullLogger<PipelineSetupReadinessService>.Instance);

    public PipelineUndefinedVariablesReadinessTests()
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([(21, "w7-deploy-prod", DeployYaml)]);
        _config.ReadAsync(ProjectId, ".pipeline/configs/env/production.env", null, null, Arg.Any<CancellationToken>()).Returns(Sample);
        _pipelines.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(
            [new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "w7", DefaultBranch = "main", IsEmpty = false }]);
    }

    private async Task<IReadOnlyList<string>> UndefinedAsync(PipelineSetupReadinessService sut)
    {
        var readiness = await sut.CheckProjectAsync(ProjectId, pipelineId: null, organizationId: 3, Ct);
        return readiness.Checks.SingleOrDefault(check => check.Kind == PipelineSetupReadinessKind.UndefinedVariables)?.Items ?? [];
    }

    [Fact]
    public async Task EveryNameNothingDefines_IsNamed_AmongTheStepsReferencesAndTheEnvironmentSamplePlaceholders()
    {
        var names = await UndefinedAsync(Sut());

        // $(targetColour) is a run parameter and $(BUILD_BUILDID) a control-plane variable: both are provided by the run.
        Assert.Equal(["API_HOST", "APPNAME", "DB_USER", "DEPLOY_ENV", "SECRET_FROM_VAULT"], names);
    }

    [Fact]
    public async Task ANameALibraryOrAVaultDefines_IsNotReported()
    {
        var names = await UndefinedAsync(Sut("API_HOST", "APPNAME", "DB_USER", "SECRET_FROM_VAULT"));

        Assert.Equal(["DEPLOY_ENV"], names);
    }

    [Fact]
    public async Task R56_ASecretLookingName_GoesToThePipelinesVault_AndAPlainOneToItsLibrary_ByNameAndId()
    {
        _requirements.FindLibraryIdsAsync(Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["w7.prod"] = 5 });
        _requirements.FindVaultIdsAsync(Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["w7.vault"] = 9 });

        var check = await UndefinedCheckAsync(SecretsYaml);

        var rows = check.UndefinedVariables.ToDictionary(row => row.Name);
        Assert.Equal(["API_HOST", "DB_PASSWORD", "JWT_KEY", "PORT_FRONT"], rows.Keys.Order());
        Assert.All(new[] { "DB_PASSWORD", "JWT_KEY" }, name =>
        {
            Assert.Equal(PipelineSetupReadinessContainerKind.Vault, rows[name].Container);
            Assert.Equal(("w7.vault", 9), (rows[name].OwnerName, rows[name].OwnerId));
        });
        // The first library that exists wins over a declared one nobody created yet (w7.share).
        Assert.All(new[] { "API_HOST", "PORT_FRONT" }, name =>
        {
            Assert.Equal(PipelineSetupReadinessContainerKind.Library, rows[name].Container);
            Assert.Equal(("w7.prod", 5), (rows[name].OwnerName, rows[name].OwnerId));
        });
    }

    [Fact]
    public async Task R56_WhenTheDeclaredHolderDoesNotExist_ItIsNamedWithoutAnId_AndWhenNoneIsDeclared_ItHasNoName()
    {
        var declaredOnly = await UndefinedCheckAsync(SecretsYaml);
        var vault = declaredOnly.UndefinedVariables.Single(row => row.Name == "DB_PASSWORD");
        Assert.Equal(("w7.vault", null), (vault.OwnerName, vault.OwnerId));
        var library = declaredOnly.UndefinedVariables.Single(row => row.Name == "API_HOST");
        Assert.Equal(("w7.share", null), (library.OwnerName, library.OwnerId));

        var none = await UndefinedCheckAsync("""
            name: w7-deploy-prod
            stages:
              - name: Deploy
                steps:
                  - name: Run
                    shell: echo $(DB_PASSWORD) $(API_HOST)
            """);
        Assert.All(none.UndefinedVariables, row => Assert.Equal((string.Empty, (int?)null), (row.OwnerName, row.OwnerId)));
        Assert.Equal(PipelineSetupReadinessContainerKind.Vault, none.UndefinedVariables.Single(row => row.Name == "DB_PASSWORD").Container);
        Assert.Equal(PipelineSetupReadinessContainerKind.Library, none.UndefinedVariables.Single(row => row.Name == "API_HOST").Container);
    }

    [Fact]
    public async Task WhenEverythingIsDefined_NoFindingIsRaised()
    {
        var names = await UndefinedAsync(Sut("API_HOST", "APPNAME", "DB_USER", "DEPLOY_ENV", "SECRET_FROM_VAULT"));

        Assert.Empty(names);
    }

    [Fact]
    public async Task TheSetupWizardsPreCreationCheck_DoesNotRaiseIt()
    {
        // A template the wizard has not created yet has no variable to define: the finding is for existing pipelines.
        _pipelines.FindSharedTemplateByNameAsync("deploy", Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.PipelineTemplate { Id = 1, Name = "deploy" });
        _templates.GetTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateDto { Id = 1, Name = "deploy", YamlContent = DeployYaml });

        var readiness = await Sut().CheckAsync(ProjectId, ["deploy"], organizationId: 3, Ct);


        Assert.DoesNotContain(readiness.Checks, check => check.Kind == PipelineSetupReadinessKind.UndefinedVariables);
    }
}
