// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.PortRegistry;
using Aetheus.Shared.Components.VariableLibraries;
using Aetheus.Shared.Components.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-11: whether the API gets its own virtual hosts follows the application shape the user chose (the template the
/// wizard writes), never the Compose file. The Compose file used here publishes a back port, as a monolith's may.</summary>
public sealed class PipelineProductionSetupShapeTests
{
    private const int ProjectId = 7;
    private const int LibraryId = 101;
    private const int VaultId = 201;
    private const int RepositoryId = 5;
    private const string ApiHttp = ".pipeline/configs/apache/api-http.conf";
    private const string ApiHttps = ".pipeline/configs/apache/api-https.conf";
    private const string AppHttps = ".pipeline/configs/apache/app-https.conf";
    private static readonly string ComposePath = PipelineProductionSetupServiceTests.ComposePath;

    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly PipelineProductionSetupService _sut;
    private IReadOnlyDictionary<string, string> _configurationFiles = new Dictionary<string, string>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PipelineProductionSetupShapeTests()
    {
        var pipelines = Substitute.For<IPipelineRepository>();
        var libraries = Substitute.For<IVariableLibraryService>();
        var vaults = Substitute.For<IVaultService>();
        var ports = Substitute.For<IPortAllocationService>();
        var configuration = Substitute.For<IProjectConfigRepositoryService>();
        _sut = new PipelineProductionSetupService(
            pipelines, _git, libraries, vaults, ports, configuration, Substitute.For<IAuditService>(),
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
        ports.GetTargetsAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(new PortAllocationTargetsDto
        {
            Servers = [new PortAllocationTargetDto { ServerId = 4, ServerName = "vps" }],
            ProjectId = ProjectId
        });
        libraries.ImportEntriesAsync(LibraryId, Arg.Any<List<CreateVariableEntryRequest>>(), Arg.Any<CancellationToken>()).Returns(0);
        vaults.ImportSecretsAsync(VaultId, Arg.Any<List<CreateVaultSecretRequest>>(), Arg.Any<CancellationToken>()).Returns(0);
        configuration.EnsureAsync(ProjectId, Arg.Do<IReadOnlyDictionary<string, string>>(files => _configurationFiles = files), Arg.Any<CancellationToken>())
            .Returns(new ProjectConfigRepositoryDto { Id = 9, Name = "Wordmorrow configuration", Slug = "wordmorrow-config", Created = true });
        TheRepositoryHoldsTheComposeFile(PipelineProductionSetupServiceTests.W6Compose);
    }

    private void TheRepositoryHoldsTheComposeFile(string compose)
    {
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = RepositoryId, ProjectId = ProjectId, DefaultBranch = "main" }]);
        _git.GetTreeAsync(RepositoryId, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => TreeOf((string?)call.ArgAt<string?>(2)));
        _git.GetBlobAsync(RepositoryId, "main", ComposePath, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = ComposePath, Content = compose });
    }

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

    private static PipelineProductionApplyRequest Request(bool monolith, string? strategy = null) => new()
    {
        ProjectId = ProjectId,
        ComposeFile = ComposePath,
        Server = "vps",
        Strategy = strategy,
        Monolith = monolith
    };

    [Fact]
    public async Task AMonolith_GetsNoApiVirtualHost_EvenWhenItsComposeFilePublishesABackPort()
    {
        var result = await _sut.ApplyAsync(Request(monolith: true), Ct);

        Assert.False(result.DeclaresApi);
        Assert.Equal("replace", result.Strategy);
        Assert.DoesNotContain(ApiHttp, _configurationFiles.Keys);
        Assert.DoesNotContain(ApiHttps, _configurationFiles.Keys);
        Assert.Contains(AppHttps, _configurationFiles.Keys);
    }

    [Fact]
    public async Task AFrontAndAnApi_GetTheApiVirtualHosts()
    {
        var result = await _sut.ApplyAsync(Request(monolith: false), Ct);

        Assert.True(result.DeclaresApi);
        Assert.Contains(ApiHttp, _configurationFiles.Keys);
        Assert.Contains(ApiHttps, _configurationFiles.Keys);
    }

    [Fact]
    public async Task AFrontAndAnApi_GetTheApiVirtualHosts_EvenWhenItsComposeFilePublishesNoBackPort()
    {
        TheRepositoryHoldsTheComposeFile("""
            services:
              front:
                ports:
                  - "127.0.0.1:${PORT_FRONT:?}:80"
            """);

        var result = await _sut.ApplyAsync(Request(monolith: false), Ct);

        Assert.True(result.DeclaresApi);
        Assert.Contains(ApiHttps, _configurationFiles.Keys);
    }

    [Fact]
    public async Task AMonolith_ChosenWithTheBlueGreenStrategy_IsRefused_AsItsTemplateRunsTheReplacementOnly()
    {
        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => _sut.ApplyAsync(Request(monolith: true, strategy: ProductionStrategy.BlueGreen), Ct));

        Assert.Contains("monolith", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_configurationFiles);
    }
}
