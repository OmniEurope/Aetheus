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

/// <summary>R-66, R-68: the production setup also plans the variables the repository's own environment sample and Apache
/// templates read (W7: APPNAME, DB_USER, DEPLOY_ENV, the secrets, and PORT_FRONT / PORT_BACK in the virtual hosts), with
/// the convention values where it knows them and the others listed as values to give.</summary>
public sealed class PipelineProductionSetupTemplatesTests
{
    private const int ProjectId = 7;
    private const int LibraryId = 101;
    private const int VaultId = 201;
    private const string ComposePath = "deploy/compose/remote.compose.yml";
    private const string EnvPath = ".pipeline/configs/env/production.env";
    private const string AppVhost = ".pipeline/configs/apache/app-https.conf";
    private const string ApiVhost = ".pipeline/configs/apache/api-https.conf";

    // A Compose file that reads almost nothing: everything else W7 needs is named by the repository's own templates.
    private const string QuietCompose = "services:\n  front:\n    container_name: ${APPNAME:?}\n";

    private const string EnvSample = """
        # Environment of the production deployment.
        APPNAME=#{APPNAME}#
        DB_PASSWORD=#{DB_PASSWORD}#
        DB_USER=#{DB_USER}#
        ENV=#{DEPLOY_ENV}#
        JWT_KEY=#{JWT_KEY}#
        MAIL_RELAY=#{MAIL_RELAY}#
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPipelineRepository _pipelines = Substitute.For<IPipelineRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IVariableLibraryService _libraries = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaults = Substitute.For<IVaultService>();
    private readonly IPortAllocationService _ports = Substitute.For<IPortAllocationService>();
    private readonly IProjectConfigRepositoryService _configuration = Substitute.For<IProjectConfigRepositoryService>();
    private readonly List<CreateVariableEntryRequest> _libraryAdds = [];
    private readonly List<CreateVaultSecretRequest> _vaultAdds = [];
    private readonly PipelineProductionSetupService _sut;

    public PipelineProductionSetupTemplatesTests()
    {
        _sut = new PipelineProductionSetupService(
            _pipelines, _git, _libraries, _vaults, _ports, _configuration,
            Substitute.For<IAuditService>(), Substitute.For<IPipelineSetupServerRepository>(),
            NullLogger<PipelineProductionSetupService>.Instance);
        _pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Wordmorrow");
        _configuration.EnsureAsync(ProjectId, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectConfigRepositoryDto { Id = 9, Name = "Wordmorrow configuration", Slug = "wordmorrow-config", Created = true });
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = LibraryId, Name = "wordmorrow.prod", ProjectId = ProjectId }]
            });
        _vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto>
            {
                Items = [new VaultDto { Id = VaultId, Name = "wordmorrow.prod", ProjectId = ProjectId }]
            });
        _libraries.ExportEntriesAsync(LibraryId, Arg.Any<CancellationToken>()).Returns([]);
        _vaults.ExportSecretKeysAsync(VaultId, Arg.Any<CancellationToken>()).Returns([]);
        _ports.GetTargetsAsync(LibraryId, Arg.Any<CancellationToken>()).Returns(new PortAllocationTargetsDto
        {
            Servers = [new PortAllocationTargetDto { ServerId = 4, ServerName = "vps" }],
            ProjectId = ProjectId
        });
        _libraries.ImportEntriesAsync(LibraryId, Arg.Do<List<CreateVariableEntryRequest>>(_libraryAdds.AddRange), Arg.Any<CancellationToken>()).Returns(0);
        _vaults.ImportSecretsAsync(VaultId, Arg.Do<List<CreateVaultSecretRequest>>(_vaultAdds.AddRange), Arg.Any<CancellationToken>()).Returns(0);
    }

    /// <summary>A repository holding the Compose file and these other files, at their paths.</summary>
    private void Repository(Dictionary<string, string> files)
    {
        files[ComposePath] = QuietCompose;
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = 5, ProjectId = ProjectId, DefaultBranch = "main" }]);
        _git.GetTreeAsync(5, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Listing(call.ArgAt<string?>(2), files.Keys));
        _git.GetBlobAsync(5, "main", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => files.TryGetValue(call.ArgAt<string>(2), out var content)
                ? new GitLightBlobDto { Path = call.ArgAt<string>(2), Content = content }
                : throw new InvalidOperationException("no such file"));
    }

    private static List<GitLightTreeEntryDto> Listing(string? directory, IEnumerable<string> paths)
    {
        var prefix = string.IsNullOrEmpty(directory) ? string.Empty : directory + "/";
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

    private static Dictionary<string, string> W7Templates() => new()
    {
        [EnvPath] = EnvSample,
        [AppVhost] = "ProxyPass / http://127.0.0.1:#{PORT_FRONT}#/\nServerName #{APP_HOST}#\n",
        [ApiVhost] = "ProxyPass / http://127.0.0.1:#{PORT_BACK}#/\nServerName #{API_HOST}#\n"
    };

    private static PipelineProductionApplyRequest Request() => new()
    {
        ProjectId = ProjectId,
        ComposeFile = ComposePath,
        Server = string.Empty,
        Values = new() { ["DOMAIN"] = "w7.test" }
    };

    [Fact]
    public async Task TheAnalysis_PlansTheVariablesOfTheEnvSample_WithTheConventionWhereItKnowsOne()
    {
        Repository(W7Templates());

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        var kinds = analysis.Variables.ToDictionary(variable => variable.Name, variable => variable.Kind);
        Assert.Equal(ProductionVariableKind.DefaultValue, kinds["APPNAME"]);
        Assert.Equal(ProductionVariableKind.DefaultValue, kinds["DB_USER"]);
        Assert.Equal(ProductionVariableKind.DefaultValue, kinds["DEPLOY_ENV"]);
        Assert.Equal(ProductionVariableKind.GeneratedSecret, kinds["DB_PASSWORD"]);
        Assert.Equal(ProductionVariableKind.GeneratedSecret, kinds["JWT_KEY"]);
        // The one the convention does not know is a value to give, listed with the others.
        Assert.Equal(ProductionVariableKind.NeedsValue, kinds["MAIL_RELAY"]);
        // What the deployment provides itself (the hosts of the virtual hosts) is never asked.
        Assert.DoesNotContain("APP_HOST", kinds.Keys);
        Assert.DoesNotContain("API_HOST", kinds.Keys);
    }

    [Fact]
    public async Task TheApacheTemplatesPorts_AreDetected_ReservedAndShownInTheToDoList()
    {
        Repository(W7Templates());

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);
        await _sut.ApplyAsync(Request(), Ct);

        Assert.True(analysis.DeclaresFrontPort);
        Assert.True(analysis.DeclaresBackPort);
        Assert.True(analysis.DeclaresApi);
        Assert.Equal(ProductionVariableKind.Port, analysis.Variables.Single(variable => variable.Name == "PORT_FRONT").Kind);
        Assert.False(analysis.Variables.Single(variable => variable.Name == "PORT_BACK").Exists);
    }

    [Fact]
    public async Task Applying_CreatesWhatTheTemplatesRead_InTheLibraryAndTheVault_AndReservesThePorts()
    {
        Repository(W7Templates());

        var result = await _sut.ApplyAsync(Request(), Ct);

        var library = _libraryAdds.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal("wordmorrow", library["APPNAME"]);
        Assert.Equal("wordmorrow", library["DB_USER"]);
        Assert.Equal("prod", library["DEPLOY_ENV"]);
        Assert.Equal(string.Empty, library["MAIL_RELAY"]);
        Assert.Equivalent(new[] { "DB_PASSWORD", "JWT_KEY" }, _vaultAdds.Select(secret => secret.Key));
        await _libraries.DidNotReceive().ImportEntriesAsync(
            LibraryId, Arg.Is<List<CreateVariableEntryRequest>>(entries => entries.Count(entry => entry.Key == "APPNAME") > 1), Ct);
        Assert.Equal(ProductionEntryOutcome.NeedsValue, result.Entries.Single(entry => entry.Name == "MAIL_RELAY").Outcome);
        Assert.True(result.NeedsValues);
        Assert.True(result.DeclaresApi);
        Assert.Contains(result.Entries, entry => entry.Name == "PORT_FRONT");
        Assert.Contains(result.Entries, entry => entry.Name == "PORT_BACK");
    }

    [Fact]
    public async Task ARepositoryWithoutTemplates_PlansOnlyWhatTheComposeFileReads()
    {
        Repository([]);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Equal(["APPNAME", "DOMAIN"], analysis.Variables.Select(variable => variable.Name).Order(StringComparer.Ordinal));
        Assert.False(analysis.DeclaresFrontPort);
        Assert.False(analysis.DeclaresBackPort);
    }

    [Fact]
    public async Task ThePlatformsOwnProject_KeepsItsOwnFiles_NothingIsReadFromItsTemplates()
    {
        _pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Aetheus");
        _libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto>
            {
                Items = [new VariableLibraryDto { Id = LibraryId, Name = "aetheus.prod", ProjectId = ProjectId }]
            });
        Repository(W7Templates());

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Equal(["APPNAME", "DOMAIN"], analysis.Variables.Select(variable => variable.Name).Order(StringComparer.Ordinal));
        Assert.False(analysis.DeclaresFrontPort);
    }
}
