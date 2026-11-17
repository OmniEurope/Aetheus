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

/// <summary>R-38 (project oe1): the deployment refuses to start on four missing Apache templates. The analysis says which of
/// the generic virtual host templates neither the configuration repository nor the source branch holds, so a project whose
/// configuration repository exists (ports and secrets all prepared) but lacks them is still offered the preparation that
/// adds them.</summary>
public sealed class PipelineProductionSetupMissingApacheTests
{
    private const int ProjectId = 21;
    private const int LibraryId = 101;
    private const int VaultId = 201;
    private const int RepositoryId = 5;
    private static readonly string ComposePath = PipelineProductionSetupServiceTests.ComposePath;
    private static readonly string[] AllFour =
    [
        ".pipeline/configs/apache/api-http.conf", ".pipeline/configs/apache/api-https.conf",
        ".pipeline/configs/apache/app-http.conf", ".pipeline/configs/apache/app-https.conf"
    ];

    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IProjectConfigRepositoryService _configuration = Substitute.For<IProjectConfigRepositoryService>();
    private readonly PipelineProductionSetupService _sut;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PipelineProductionSetupMissingApacheTests()
    {
        var pipelines = Substitute.For<IPipelineRepository>();
        var libraries = Substitute.For<IVariableLibraryService>();
        var vaults = Substitute.For<IVaultService>();
        var ports = Substitute.For<IPortAllocationService>();
        _sut = new PipelineProductionSetupService(
            pipelines, _git, libraries, vaults, ports, _configuration, Substitute.For<IAuditService>(),
            Substitute.For<IPipelineSetupServerRepository>(), NullLogger<PipelineProductionSetupService>.Instance);
        pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Oe1");
        libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = LibraryId, Name = "oe1.prod", ProjectId = ProjectId }]
            });
        vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = VaultId, Name = "oe1.prod", ProjectId = ProjectId }]
            });
        libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>()).Returns([]);
        vaults.ExportSecretKeysAsync(VaultId, Arg.Any<CancellationToken>()).Returns([]);
        ports.GetTargetsAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(new PortAllocationTargetsDto
        {
            Servers = [new PortAllocationTargetDto { ServerId = 4, ServerName = "vps" }],
            ProjectId = ProjectId
        });
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = RepositoryId, ProjectId = ProjectId, DefaultBranch = "main" }]);
        _git.GetTreeAsync(RepositoryId, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => TreeOf((string?)call.ArgAt<string?>(2)));
        _git.GetBlobAsync(RepositoryId, "main", ComposePath, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = ComposePath, Content = PipelineProductionSetupServiceTests.W6Compose });
    }

    /// <summary>The source holds the Compose file only: no <c>.pipeline</c> directory.</summary>
    private static List<GitLightTreeEntryDto> TreeOf(string? directory)
    {
        var prefix = string.IsNullOrEmpty(directory) ? string.Empty : directory + "/";
        if (!ComposePath.StartsWith(prefix, StringComparison.Ordinal)) return [];
        var rest = ComposePath[prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var name = slash < 0 ? rest : rest[..slash];
        return [new GitLightTreeEntryDto { Name = name, Path = prefix + name, Type = slash < 0 ? GitTreeEntryType.Blob : GitTreeEntryType.Tree }];
    }

    private void TheConfigurationRepositoryExistsAndHolds(params string[] paths)
    {
        _configuration.FindAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns(new GitLightRepoDto { Id = 9, ProjectId = ProjectId, Name = "oe1-config", Slug = "oe1-config", DefaultBranch = "main", IsConfiguration = true });
        _configuration.ReadAsync(ProjectId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => paths.Contains(call.ArgAt<string>(1), StringComparer.Ordinal) ? "content" : null);
    }

    [Fact]
    public async Task AConfigurationRepositoryHoldingNoApacheTemplate_ListsTheFourAsMissing()
    {
        TheConfigurationRepositoryExistsAndHolds();

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.NotNull(analysis.ConfigRepository);
        Assert.Equal(AllFour, analysis.MissingApacheTemplates);
    }

    [Fact]
    public async Task OnlyTheTemplatesNotHeldAnywhere_AreListedAsMissing()
    {
        TheConfigurationRepositoryExistsAndHolds(AllFour[2], AllFour[3]);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Equal([AllFour[0], AllFour[1]], analysis.MissingApacheTemplates);
    }

    [Fact]
    public async Task AConfigurationRepositoryHoldingEveryTemplate_ListsNone()
    {
        TheConfigurationRepositoryExistsAndHolds(AllFour);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Empty(analysis.MissingApacheTemplates);
    }
}
