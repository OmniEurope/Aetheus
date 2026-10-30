// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.PortRegistry;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R3-67: a project set up from a shipped template gets the libraries and vaults that template
/// reads. The shipped YAML goes through the real readiness check, requirement checker and provisioner;
/// only the stores (templates, libraries, vaults, git) are substitutes, and the project has none yet.
/// </summary>
public sealed class TemplateLibrariesAndVaultsProvisioningTests
{
    private const int ProjectId = 7;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineTemplateService _templates = Substitute.For<IPipelineTemplateService>();
    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IPipelineTemplateResolver _resolver = Substitute.For<IPipelineTemplateResolver>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();
    private readonly PipelineRequirementsProvisioner _sut;

    public TemplateLibrariesAndVaultsProvisioningTests()
    {
        var checker = new PipelineRequirementsChecker(_pipelines, _libraries, _vaults);
        var readiness = new PipelineSetupReadinessService(
            _templates, _pipelines, _git, Substitute.For<IGitLightRepository>(), _resolver, checker,
            Substitute.For<IProjectConfigRepositoryService>(),
            NullLogger<PipelineSetupReadinessService>.Instance);
        _sut = new PipelineRequirementsProvisioner(
            readiness, _pipelines, checker, _resolver, _libraries, _vaults,
            NullLogger<PipelineRequirementsProvisioner>.Instance);

        // The project the names are prefixed with: "Wordmorrow" is the slug "wordmorrow".
        _pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Wordmorrow");

        // A new project: no library and no vault of any name.
        _libraries.ResolveLibrariesWithNamesAsync(Arg.Any<List<string>>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>()));
        _libraries.ResolveLibrariesWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>()));
        _vaults.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>()));
        _vaults.GetVaultNamesAsync(Arg.Any<int?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>());
        _vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>());
        _libraries.CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new VariableLibraryDto { Id = 101, Name = call.Arg<CreateVariableLibraryRequest>().Name, ProjectId = ProjectId });
        _vaults.CreateVaultAsync(Arg.Any<CreateVaultRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new VaultDto { Id = 201, Name = call.Arg<CreateVaultRequest>().Name, ProjectId = ProjectId });
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(new List<GitLightRepoDto>());
        _pipelines.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 11 });
    }

    private void ShippedTemplate(string name, string resource)
    {
        _pipelines.FindSharedTemplateByNameAsync(name, Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.PipelineTemplate { Id = 1, Name = name });
        _templates.GetTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateDto { Id = 1, Name = name, YamlContent = DeliveryTemplateCatalog.ReadResource(resource) });
    }

    public static TheoryData<string, string, string[], string[]> ChangedTemplates => new()
    {
        { "application-deploy-prod", "generic/application-deploy-prod.yaml", ["wordmorrow.share", "wordmorrow.prod"], ["wordmorrow.prod"] },
        { "application-release-fast", "generic/application-release-fast.yaml", ["wordmorrow.share", "wordmorrow.prod"], [] },
        { "application-qa", "generic/application-qa.yaml", ["wordmorrow.share", "wordmorrow.qa"], ["wordmorrow.qa"] },
        { "publish-observability-packages", "generic/publish-observability-packages.yaml", [], ["wordmorrow.package"] }
    };

    [Theory]
    [MemberData(nameof(ChangedTemplates))]
    public async Task R3_67_TheWizard_CreatesInTheProject_TheLibrariesAndVaultsTheTemplateReads(
        string template, string resource, string[] libraries, string[] vaults)
    {
        ShippedTemplate(template, resource);

        var result = await _sut.ProvisionAsync(ProjectId, [template], null, null, null, Ct);

        Assert.Equal(libraries.Order(StringComparer.Ordinal), result.Libraries.Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(vaults, result.Vaults.Select(item => item.Name));
        foreach (var name in libraries)
            await _libraries.Received(1).CreateLibraryAsync(
                Arg.Is<CreateVariableLibraryRequest>(request => request.Name == name && request.ProjectId == ProjectId), Arg.Any<CancellationToken>());
        foreach (var name in vaults)
            await _vaults.Received(1).CreateVaultAsync(
                Arg.Is<CreateVaultRequest>(request => request.Name == name && request.ProjectId == ProjectId), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(ChangedTemplates))]
    public async Task R3_67_TheOverview_CreatesForAPipelineExtendingTheTemplate_TheSameLibrariesAndVaults(
        string template, string resource, string[] libraries, string[] vaults)
    {
        const string pipelineYaml = "extends: template@2";
        _pipelines.GetPipelineDefinitionsForProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns(new List<(int, string, string)> { (9, template, pipelineYaml) });
        _pipelines.GetPipelineOrganizationIdAsync(9, Arg.Any<CancellationToken>()).Returns(3);
        var yaml = DeliveryTemplateCatalog.ReadResource(resource);
        _resolver.ResolveAsync(pipelineYaml, 3, null, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateResolution(YamlParsingHelper.ParseAndValidate(yaml)!, yaml, template, 2, 2, false));

        var result = await _sut.ProvisionUnmetAsync(ProjectId, null, null, null, Ct);

        Assert.Equal(libraries.Order(StringComparer.Ordinal), result.Libraries.Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(vaults, result.Vaults.Select(item => item.Name));
    }

    /// <summary>
    /// The names the wizard creates are the names a run of the template reads: the same shipped YAML
    /// through the run's variable resolver asks the library and vault stores for exactly them.
    /// </summary>
    [Theory]
    [MemberData(nameof(ChangedTemplates))]
    public async Task R3_67_ARunOfTheTemplate_ReadsTheProjectPrefixedNamesTheWizardCreated(
        string template, string resource, string[] libraries, string[] vaults)
    {
        _ = template;
        var definition = YamlParsingHelper.ParseAndValidate(DeliveryTemplateCatalog.ReadResource(resource))!;
        _libraries.ResolveLibrariesWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(call => (new Dictionary<string, string>(), call.Arg<List<string>>().ToHashSet(StringComparer.OrdinalIgnoreCase)));
        _vaults.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), ProjectId, Arg.Any<CancellationToken>())
            .Returns(call => (new Dictionary<string, string>(), call.Arg<List<string>>().ToHashSet(StringComparer.OrdinalIgnoreCase)));
        var resolver = new PipelineVariableResolver(
            _libraries, _vaults, _pipelines, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System);

        var (_, warnings, _) = await resolver.ResolveVariablesWithWarningsAsync(definition, ProjectId, null, Ct);

        Assert.Empty(warnings);
        if (libraries.Length > 0)
            await _libraries.Received(1).ResolveLibrariesWithCrossAccessAndNamesAsync(
                Arg.Is<List<string>>(names => names.Order(StringComparer.Ordinal).SequenceEqual(libraries.Order(StringComparer.Ordinal))),
                ProjectId, Arg.Any<CancellationToken>());
        if (vaults.Length > 0)
            await _vaults.Received(1).ResolveVaultSecretsWithCrossAccessAndNamesAsync(
                Arg.Is<List<string>>(names => names.SequenceEqual(vaults)), ProjectId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R3_67_ATemplateThatReadsNoLibraryOrVault_CreatesNothing()
    {
        ShippedTemplate("application-ci", "generic/application-ci.yaml");

        var result = await _sut.ProvisionAsync(ProjectId, ["application-ci"], null, null, null, Ct);

        Assert.Empty(result.Libraries);
        Assert.Empty(result.Vaults);
        await _libraries.DidNotReceive().CreateLibraryAsync(Arg.Any<CreateVariableLibraryRequest>(), Arg.Any<CancellationToken>());
    }
}
