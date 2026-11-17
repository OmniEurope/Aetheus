// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-25: the pre-run check of a pipeline that extends a template, composed from the real pieces: the shipped
/// <c>application-release-fast</c> template through the real template resolver, the real variable resolver (libraries and
/// vaults as stores), the real readiness service. Only the stores and the repository files are substitutes. What the check
/// reports is what the launch would resolve: the vaults the pipeline really declares (its own, else its template's: a
/// non-empty child list replaces the template's), and the steps that really apply.
/// </summary>
public sealed class ReleaseFastReadinessComposedTests
{
    private const int ProjectId = 19;
    private const int PipelineId = 176;
    private const int OrganizationId = 3;
    private const string ProdVault = "wordmorrow.prod";
    private const string Sample = "DB_PASSWORD=#{DB_PASSWORD}#\nJWT_KEY=#{JWT_KEY}#\nAPPNAME=#{APPNAME}#\n";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IProjectConfigRepositoryService _config = Substitute.For<IProjectConfigRepositoryService>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();
    private readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The secrets each existing vault of the project holds.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _vaultSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProdVault] = new(StringComparer.OrdinalIgnoreCase) { ["DB_PASSWORD"] = "x", ["JWT_KEY"] = "y" }
    };

    public ReleaseFastReadinessComposedTests()
    {
        _templates["application-release-fast"] = DeliveryTemplateCatalog.ReadResource("generic/application-release-fast.yaml");
        _pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Wordmorrow");
        _pipelines.GetPipelineOrganizationIdAsync(PipelineId, Arg.Any<CancellationToken>()).Returns(OrganizationId);
        _config.ReadAsync(ProjectId, ".pipeline/configs/env/production.env", null, null, Arg.Any<CancellationToken>()).Returns(Sample);
        _pipelines.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([11]);
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(
            [new GitLightRepoDto { Id = 4, ProjectId = ProjectId, Name = "w1", DefaultBranch = "main", IsEmpty = false }]);

        // The libraries the project holds: the shared and the production one, carrying what the sample reads.
        _libraries.ResolveLibrariesWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var names = call.Arg<List<string>>();
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (names.Contains("wordmorrow.prod", StringComparer.OrdinalIgnoreCase))
                    vars["APPNAME"] = "w1";
                return (vars, names.ToHashSet(StringComparer.OrdinalIgnoreCase));
            });
        _vaults.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var names = call.Arg<List<string>>();
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in names.Where(_vaultSecrets.ContainsKey))
                {
                    found.Add(name);
                    foreach (var (key, value) in _vaultSecrets[name]) vars[key] = value;
                }
                return (vars, found);
            });
        _requirements.FindKeyHoldersAsync(Arg.Any<IReadOnlyCollection<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<PipelineKeyHolder>)[
                .. _vaultSecrets.SelectMany(vault => vault.Value.Keys.Select(key =>
                    new PipelineKeyHolder(key, PipelineSetupReadinessContainerKind.Vault, 9, vault.Key)))]);
    }

    private IPipelineTemplateRepository TemplateRepository()
    {
        var repository = Substitute.For<IPipelineTemplateRepository>();
        var id = 40;
        foreach (var (name, yaml) in _templates)
        {
            var template = new PipelineTemplate
            {
                Id = ++id,
                Name = name,
                OrganizationId = OrganizationId,
                LatestVersion = 1,
                Versions = [new PipelineTemplateVersion { Version = 1, YamlContent = yaml }]
            };
            repository.FindTemplateByNameAsync(name, OrganizationId, Arg.Any<CancellationToken>()).Returns(template);
        }
        return repository;
    }

    private async Task<PipelineSetupReadinessDto> ReadinessOfAsync(string pipelineYaml)
    {
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([(PipelineId, "w1-release-fast", pipelineYaml)]);
        var variables = new PipelineVariableResolver(
            _libraries, _vaults, _pipelines, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System);
        var sut = new PipelineSetupReadinessService(
            Substitute.For<IPipelineTemplateService>(), _pipelines, _git, Substitute.For<IGitLightRepository>(),
            new PipelineTemplateResolver(TemplateRepository()), _requirements, _config, variables,
            Substitute.For<IPipelineSetupServerRepository>(), NullLogger<PipelineSetupReadinessService>.Instance);
        return await sut.CheckProjectAsync(ProjectId, pipelineId: PipelineId, organizationId: OrganizationId, Ct);
    }

    private static PipelineSetupReadinessCheckDto? Undefined(PipelineSetupReadinessDto readiness) =>
        readiness.Checks.SingleOrDefault(check => check.Kind == PipelineSetupReadinessKind.UndefinedVariables);

    private static string Child(string extraVariables = "", string vaults = "") => $"""
        name: w1-release-fast
        extends: application-release-fast@1
        trigger: manual
        {vaults}
        variables:
          APPLICATION_PREFIX: "w1"
          {extraVariables}
        stages: []
        """;

    [Fact]
    public async Task ReplaceDeployment_OfTheShippedTemplate_RendersTheSampleFromTheVaultItDeclares_AndAsksForNothing()
    {
        var readiness = await ReadinessOfAsync(Child());

        Assert.Null(Undefined(readiness));
    }

    [Fact]
    public async Task BlueGreenDeployment_WithTheVaultDeclaredByTheTemplate_AsksForNothing()
    {
        var readiness = await ReadinessOfAsync(Child("DEPLOY_STRATEGY: \"bluegreen\""));

        Assert.Null(Undefined(readiness));
    }

    [Fact]
    public async Task BlueGreenDeployment_WhosePipelineReplacesTheVaultList_SaysTheKeysExistButAreNotDeclared()
    {
        _vaultSecrets["other.vault"] = new(StringComparer.OrdinalIgnoreCase) { ["UNRELATED"] = "z" };

        var readiness = await ReadinessOfAsync(Child("DEPLOY_STRATEGY: \"bluegreen\"", "vaults:\n  - other.vault"));

        var check = Undefined(readiness);
        Assert.NotNull(check);
        Assert.Equal(["DB_PASSWORD", "JWT_KEY"], check.Items);
        Assert.All(check.UndefinedVariables, row => Assert.Equal(ProdVault, row.ExistingHolderName));
    }

    [Fact]
    public async Task BlueGreenDeployment_WithTheVaultDeclaredByThePipeline_AsksForNothing()
    {
        var readiness = await ReadinessOfAsync(Child("DEPLOY_STRATEGY: \"bluegreen\"", $"vaults:\n  - {ProdVault}"));

        Assert.Null(Undefined(readiness));
    }

    [Fact]
    public async Task AVaultTheTemplateDeclares_IsInheritedByThePipeline_ThatDeclaresNone()
    {
        _templates["with-vault"] = """
            name: with-vault
            vaults:
              - wordmorrow.prod
            variables:
              DEPLOY_ENV_SAMPLE: ".pipeline/configs/env/production.env"
              APPNAME: "w1"
            stages:
              - name: Render
                steps:
                  - name: Render the environment file
                    env_template: "$(DEPLOY_ENV_SAMPLE)"
                    shell: echo render
            """;
        var readiness = await ReadinessOfAsync("name: w1-x\nextends: with-vault@1\ntrigger: manual\nstages: []\n");

        Assert.Null(Undefined(readiness));
    }

    [Fact]
    public async Task AVaultListOfThePipeline_ReplacesTheTemplatesList_SoItsKeysAreNoLongerRead()
    {
        _vaultSecrets["other.vault"] = new(StringComparer.OrdinalIgnoreCase) { ["UNRELATED"] = "z" };
        _templates["with-vault"] = """
            name: with-vault
            vaults:
              - wordmorrow.prod
            variables:
              DEPLOY_ENV_SAMPLE: ".pipeline/configs/env/production.env"
              APPNAME: "w1"
            stages:
              - name: Render
                steps:
                  - name: Render the environment file
                    env_template: "$(DEPLOY_ENV_SAMPLE)"
                    shell: echo render
            """;
        var readiness = await ReadinessOfAsync("name: w1-x\nextends: with-vault@1\ntrigger: manual\nvaults:\n  - other.vault\nstages: []\n");

        var check = Undefined(readiness);
        Assert.NotNull(check);
        Assert.Equal(["DB_PASSWORD", "JWT_KEY"], check.Items);
    }
}
