// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.PortRegistry;
using Aetheus.Shared.Components.VariableLibraries;
using Aetheus.Shared.Components.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Recette R-9 and R-15: the production setup reads the Compose file of the branch the wizard creates the pipelines
/// on. A repository whose default branch is main and whose work, Compose file included, lives on develop used to be read on
/// main: nothing was found, so the production pipeline was written without <c>COMPOSE_FILE</c> nor <c>DEPLOY_HAS_API</c> and
/// the library <c>prod</c> without <c>APPNAME</c>, <c>DB_USER</c> and <c>DEPLOY_ENV</c>.</summary>
public sealed class PipelineProductionSetupBranchTests
{
    private const int ProjectId = 7;
    private const int LibraryId = 101;
    private const int VaultId = 201;
    private const int RepositoryId = 5;
    private static readonly string ComposePath = PipelineProductionSetupServiceTests.ComposePath;

    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IPortAllocationService _ports = Substitute.For<IPortAllocationService>();
    private readonly List<CreateVariableEntryRequest> _libraryAdds = [];
    private readonly PipelineProductionSetupService _sut;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PipelineProductionSetupBranchTests()
    {
        var pipelines = Substitute.For<IPipelineRepository>();
        var libraries = Substitute.For<IVariableLibraryService>();
        var vaults = Substitute.For<IVaultService>();
        var configuration = Substitute.For<IProjectConfigRepositoryService>();
        _sut = new PipelineProductionSetupService(
            pipelines, _git, libraries, vaults, _ports, configuration, Substitute.For<IAuditService>(),
            Substitute.For<IPipelineSetupServerRepository>(), NullLogger<PipelineProductionSetupService>.Instance);
        pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Wordmorrow");
        libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = LibraryId, Name = "wordmorrow.prod", ProjectId = ProjectId }]
            });
        vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = VaultId, Name = "wordmorrow.prod", ProjectId = ProjectId }]
            });
        libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>()).Returns([]);
        vaults.ExportSecretKeysAsync(VaultId, Arg.Any<CancellationToken>()).Returns([]);
        _ports.GetTargetsAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(new PortAllocationTargetsDto
        {
            Servers = [new PortAllocationTargetDto { ServerId = 4, ServerName = "vps" }],
            ProjectId = ProjectId
        });
        libraries.ImportEntriesAsync(LibraryId, Arg.Do<List<CreateVariableEntryRequest>>(_libraryAdds.AddRange), Arg.Any<CancellationToken>()).Returns(0);
        vaults.ImportSecretsAsync(VaultId, Arg.Any<List<CreateVaultSecretRequest>>(), Arg.Any<CancellationToken>()).Returns(0);
        configuration.EnsureAsync(ProjectId, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectConfigRepositoryDto { Id = 9, Name = "Wordmorrow configuration", Slug = "wordmorrow-config", Created = true });
        DevelopHoldsTheComposeFile();
    }

    /// <summary>The default branch is main and holds nothing; develop holds the production Compose file.</summary>
    private void DevelopHoldsTheComposeFile()
    {
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = RepositoryId, ProjectId = ProjectId, DefaultBranch = "main" }]);
        _git.GetBranchesAsync(RepositoryId, Arg.Any<CancellationToken>())
            .Returns([new GitLightBranchDto { Name = "main", IsDefault = true }, new GitLightBranchDto { Name = "develop" }]);
        _git.GetTreeAsync(RepositoryId, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        _git.GetTreeAsync(RepositoryId, "develop", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => TreeOf((string?)call.ArgAt<string?>(2)));
        _git.GetBlobAsync(RepositoryId, "develop", ComposePath, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = ComposePath, Content = PipelineProductionSetupServiceTests.W6Compose });
    }

    /// <summary>The listing of one directory of a tree that only holds <see cref="ComposePath"/>.</summary>
    private static List<GitLightTreeEntryDto> TreeOf(string? directory)
    {
        var prefix = string.IsNullOrEmpty(directory) ? string.Empty : directory + "/";
        if (!ComposePath.StartsWith(prefix, StringComparison.Ordinal)) return [];
        var rest = ComposePath[prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var name = slash < 0 ? rest : rest[..slash];
        return
        [
            new GitLightTreeEntryDto
            {
                Name = name,
                Path = prefix + name,
                Type = slash < 0 ? GitTreeEntryType.Blob : GitTreeEntryType.Tree
            }
        ];
    }

    [Fact]
    public async Task WithoutABranch_TheDefaultBranchIsRead_AndItsMissingComposeFileIsSaid()
    {
        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.True(analysis.Readable);
        Assert.Equal("main", analysis.Branch);
        Assert.Null(analysis.ComposeFile);
    }

    [Fact]
    public async Task TheAnalysis_OfTheBranchTheWizardChose_FindsTheComposeFileThatOnlyThatBranchHolds()
    {
        var analysis = await _sut.AnalyzeAsync(ProjectId, null, "develop", ProductionServerScope.None, Ct);

        Assert.Equal("develop", analysis.Branch);
        Assert.Equal(ComposePath, analysis.ComposeFile);
        Assert.True(analysis.DeclaresApi);
        Assert.Equal("replace", analysis.Strategy);
        // R-15: the three names the deployment stopped on carry their convention, none is asked.
        var conventions = analysis.Variables.Where(variable => variable.Kind == ProductionVariableKind.DefaultValue)
            .ToDictionary(variable => variable.Name, variable => variable.DefaultValue);
        Assert.Equal("wordmorrow", conventions["APPNAME"]);
        Assert.Equal("wordmorrow", conventions["DB_USER"]);
        Assert.Equal("prod", conventions["ENV"]); // held under DEPLOY_ENV in the library (the agent refuses a step variable named ENV)
        Assert.DoesNotContain(analysis.Variables, variable =>
            variable.Name is "APPNAME" or "DB_USER" or "ENV" && variable.Kind == ProductionVariableKind.NeedsValue);
    }

    [Fact]
    public async Task ABranchTheRepositoryDoesNotHold_ReadsTheDefaultBranch()
    {
        var analysis = await _sut.AnalyzeAsync(ProjectId, null, "feature/none", ProductionServerScope.None, Ct);

        Assert.Equal("main", analysis.Branch);
        Assert.Null(analysis.ComposeFile);
    }

    [Fact]
    public async Task Applying_OnTheBranchTheWizardChose_WritesTheValuesTheDeploymentStoppedOn()
    {
        var result = await _sut.ApplyAsync(new PipelineProductionApplyRequest
        {
            ProjectId = ProjectId,
            ComposeFile = ComposePath,
            Server = "vps",
            Branch = "develop",
            Values = new Dictionary<string, string> { ["DOMAIN"] = "example.test" }
        }, Ct);

        Assert.Equal(ComposePath, result.ComposeFile);
        Assert.True(result.DeclaresApi);
        var library = _libraryAdds.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("wordmorrow", library["APPNAME"]);
        Assert.Equal("wordmorrow", library["DB_USER"]);
        Assert.Equal("prod", library["DEPLOY_ENV"]);
    }

    [Fact]
    public async Task Applying_WithoutTheBranch_RefusesTheComposeFileOnlyDevelopHolds()
    {
        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => _sut.ApplyAsync(new PipelineProductionApplyRequest
        {
            ProjectId = ProjectId,
            ComposeFile = ComposePath,
            Server = "vps"
        }, Ct));
    }
}
