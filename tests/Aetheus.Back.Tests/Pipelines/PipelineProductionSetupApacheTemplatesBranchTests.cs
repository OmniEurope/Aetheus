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

/// <summary>R-6, R-7 (W8): the Compose file and the Apache templates may live only on <c>develop</c> while the default branch is
/// <c>main</c> and empty. The setup reads them on the branch the wizard chose, reserves <c>PORT_FRONT</c> and <c>PORT_BACK</c> in the
/// port registry and writes them to the production library; when the templates cannot be read the analysis says so and the setup
/// refuses to go on, instead of preparing production without the two ports.</summary>
public sealed class PipelineProductionSetupApacheTemplatesBranchTests
{
    private const int ProjectId = 7;
    private const int LibraryId = 101;
    private const int VaultId = 201;
    private const int RepositoryId = 5;
    private const int ServerId = 4;
    private const string ComposePath = "deploy/compose/remote.compose.yml";
    private const string AppVhost = ".pipeline/configs/apache/app-https.conf";
    private const string ApiVhost = ".pipeline/configs/apache/api-https.conf";
    private const string ApacheDirectory = ".pipeline/configs/apache";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IPortAllocationService _ports = Substitute.For<IPortAllocationService>();
    private readonly PipelineProductionSetupService _sut;

    public PipelineProductionSetupApacheTemplatesBranchTests()
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
            Servers = [new PortAllocationTargetDto { ServerId = ServerId, ServerName = "vps" }],
            ProjectId = ProjectId
        });
        libraries.ImportEntriesAsync(LibraryId, Arg.Any<List<CreateVariableEntryRequest>>(), Arg.Any<CancellationToken>()).Returns(0);
        vaults.ImportSecretsAsync(VaultId, Arg.Any<List<CreateVaultSecretRequest>>(), Arg.Any<CancellationToken>()).Returns(0);
        configuration.EnsureAsync(ProjectId, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectConfigRepositoryDto { Id = 9, Name = "Wordmorrow configuration", Slug = "wordmorrow-config", Created = true });
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = RepositoryId, ProjectId = ProjectId, DefaultBranch = "main" }]);
        _git.GetBranchesAsync(RepositoryId, Arg.Any<CancellationToken>())
            .Returns([new GitLightBranchDto { Name = "main", IsDefault = true }, new GitLightBranchDto { Name = "develop" }]);
        _git.GetTreeAsync(RepositoryId, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    /// <summary>Develop holds the Compose file and both Apache templates; main holds nothing.</summary>
    private void DevelopHoldsTheFiles(Func<string, bool>? unlistable = null, Func<string, bool>? unreadable = null)
    {
        var files = new Dictionary<string, string>
        {
            [ComposePath] = "services:\n  front:\n    container_name: ${APPNAME:?}\n",
            [AppVhost] = "ProxyPass / http://127.0.0.1:#{PORT_FRONT}#/\nServerName #{APP_HOST}#\n",
            [ApiVhost] = "ProxyPass / http://127.0.0.1:#{PORT_BACK}#/\nServerName #{API_HOST}#\n"
        };
        _git.GetTreeAsync(RepositoryId, "develop", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var directory = call.ArgAt<string?>(2) ?? string.Empty;
                return unlistable?.Invoke(directory) == true
                    ? throw new InvalidOperationException("git is not answering")
                    : Listing(directory, files.Keys);
            });
        _git.GetBlobAsync(RepositoryId, "develop", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => unreadable?.Invoke(call.ArgAt<string>(2)) == true || !files.ContainsKey(call.ArgAt<string>(2))
                ? throw new InvalidOperationException("git is not answering")
                : new GitLightBlobDto { Path = call.ArgAt<string>(2), Content = files[call.ArgAt<string>(2)] });
    }

    private static List<GitLightTreeEntryDto> Listing(string directory, IEnumerable<string> paths)
    {
        var prefix = directory.Length == 0 ? string.Empty : directory + "/";
        var entries = new List<GitLightTreeEntryDto>();
        foreach (var child in paths.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)).Select(path => path[prefix.Length..]))
        {
            var slash = child.IndexOf('/', StringComparison.Ordinal);
            var name = slash < 0 ? child : child[..slash];
            if (entries.Exists(entry => entry.Name == name)) continue;
            entries.Add(new GitLightTreeEntryDto
            {
                Name = name,
                Path = prefix + name,
                Type = slash < 0 ? GitTreeEntryType.Blob : GitTreeEntryType.Tree
            });
        }
        return entries;
    }

    private static PipelineProductionApplyRequest Request(string? branch) => new()
    {
        ProjectId = ProjectId,
        ComposeFile = ComposePath,
        Server = "vps",
        Branch = branch,
        Values = new Dictionary<string, string> { ["DOMAIN"] = "w8.test" }
    };

    [Fact]
    public async Task TheComposeFileAndTheApacheTemplates_OnlyOnDevelop_AreReadOnTheBranchTheWizardChose()
    {
        DevelopHoldsTheFiles();

        var onDefault = await _sut.AnalyzeAsync(ProjectId, null, Ct);
        var onDevelop = await _sut.AnalyzeAsync(ProjectId, null, "develop", ProductionServerScope.None, Ct);

        Assert.Null(onDefault.ComposeFile);
        Assert.False(onDefault.DeclaresFrontPort);
        Assert.Equal(ComposePath, onDevelop.ComposeFile);
        Assert.True(onDevelop.DeclaresFrontPort);
        Assert.True(onDevelop.DeclaresBackPort);
        Assert.True(onDevelop.DeclaresApi);
        Assert.False(onDevelop.TemplatesUnreadable);
        Assert.Equal(ProductionVariableKind.Port, onDevelop.Variables.Single(variable => variable.Name == "PORT_FRONT").Kind);
        Assert.Equal(ProductionVariableKind.Port, onDevelop.Variables.Single(variable => variable.Name == "PORT_BACK").Kind);
    }

    [Fact]
    public async Task Applying_OnDevelop_ReservesPortFrontAndPortBackInTheRegistryForTheServer()
    {
        DevelopHoldsTheFiles();
        _ports.AllocateIntoLibraryAsync(LibraryId, ServerId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _sut.ApplyAsync(Request("develop"), Ct);

        await _ports.Received(1).AllocateIntoLibraryAsync(
            LibraryId, ServerId,
            Arg.Is<IReadOnlyList<string>>(keys => keys.Contains("PORT_FRONT") && keys.Contains("PORT_BACK")), Ct);
        Assert.Equal(ProductionEntryOutcome.Allocated, result.Entries.Single(entry => entry.Name == "PORT_FRONT").Outcome);
        Assert.Equal(ProductionEntryOutcome.Allocated, result.Entries.Single(entry => entry.Name == "PORT_BACK").Outcome);
    }

    [Fact]
    public async Task AnApacheDirectoryThatCannotBeListed_IsSaidUnreadable_AndTheSetupRefusesToGoOn()
    {
        DevelopHoldsTheFiles(unlistable: directory => directory == ApacheDirectory);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, "develop", ProductionServerScope.None, Ct);

        Assert.True(analysis.Readable);
        Assert.Equal(ComposePath, analysis.ComposeFile);
        Assert.True(analysis.TemplatesUnreadable);
        Assert.False(analysis.DeclaresFrontPort);
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(Request("develop"), Ct));
        await _ports.DidNotReceiveWithAnyArgs().AllocateIntoLibraryAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task AnApacheTemplateThatCannotBeRead_IsSaidUnreadable_AndTheSetupRefusesToGoOn()
    {
        DevelopHoldsTheFiles(unreadable: path => path == ApiVhost);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, "develop", ProductionServerScope.None, Ct);

        Assert.True(analysis.TemplatesUnreadable);
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(Request("develop"), Ct));
    }
}
