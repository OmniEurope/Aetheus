// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.Pipelines;
using Aetheus.Shared.Components.VariableLibraries;
using Aetheus.Shared.Components.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-38 (project oe1, external Git source whose mirror holds no commit): the Compose file cannot be read, so the full
/// production setup is refused, and the four generic Apache templates were never created (the deployment then refused to start
/// on them). They depend only on the shape and the strategy the user chose, so they are now created in the configuration
/// repository Aetheus manages for the project, without the Compose file. The real setup and configuration repository services
/// run here over doubles of the Git service and CLI: this proves the path through both services and which repository is written,
/// not the real Git repository on disk (the orchestrator's browser proof covers that).</summary>
public sealed class PipelineProductionSetupApacheWithoutComposeTests
{
    private const int ProjectId = 21;
    private const int MirrorId = 5;
    private const int ConfigId = 9;
    private const string MirrorSlug = "omnieurope-oe1";
    private const string ConfigSlug = "oe1-config";
    private const string AppHttp = ".pipeline/configs/apache/app-http.conf";
    private const string AppHttps = ".pipeline/configs/apache/app-https.conf";
    private const string ApiHttp = ".pipeline/configs/apache/api-http.conf";
    private const string ApiHttps = ".pipeline/configs/apache/api-https.conf";

    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly PipelineProductionSetupService _sut;
    private readonly List<(string DiskPath, IReadOnlyList<(string RelativePath, string Content)> Files)> _commits = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly GitLightRepoDto ConfigRepository =
        new() { Id = ConfigId, ProjectId = ProjectId, Name = ConfigSlug, Slug = ConfigSlug, DefaultBranch = "main", IsConfiguration = true };

    public PipelineProductionSetupApacheWithoutComposeTests()
    {
        var pipelines = Substitute.For<IPipelineRepository>();
        var libraries = Substitute.For<IVariableLibraryService>();
        var vaults = Substitute.For<IVaultService>();
        pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("Oe1");
        libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto> { Items = [] });
        vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto> { Items = [] });

        // The external source: its mirror was never synchronized, so no Compose file can be read.
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightRepoDto
            {
                Id = MirrorId, ProjectId = ProjectId, Name = "OE1", Slug = MirrorSlug, DefaultBranch = "main",
                IsEmpty = true, IsExternalMirror = true
            }
        ]);
        // The project holds no configuration repository yet (the state oe1 was left in).
        _git.GetAccessibleRepositoriesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>()).Returns([]);
        _git.ResolveDiskPath(Arg.Any<int>(), Arg.Any<string>()).Returns(call => $"/repos/{call.ArgAt<int>(0)}/{call.ArgAt<string>(1)}");
        _git.CreateConfigurationRepositoryAsync(Arg.Any<CreateGitLightRepoRequest>(), Arg.Any<CancellationToken>()).Returns(ConfigRepository);
        _cli.CommitFilesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(string RelativePath, string Content)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _commits.Add((call.ArgAt<string>(0), call.ArgAt<IReadOnlyList<(string RelativePath, string Content)>>(2)));
                return (true, "abc123", (string?)null);
            });

        var configuration = new ProjectConfigRepositoryService(
            _git, _cli, Substitute.For<IGitCommitRecorder>(), Substitute.For<IPipelineGitService>(), pipelines,
            NullLogger<ProjectConfigRepositoryService>.Instance);
        _sut = new PipelineProductionSetupService(
            pipelines, _git, libraries, vaults, Substitute.For<IPortAllocationService>(), configuration,
            Substitute.For<IAuditService>(), Substitute.For<IPipelineSetupServerRepository>(),
            NullLogger<PipelineProductionSetupService>.Instance);
    }

    private void TheConfigurationRepositoryExists() =>
        _git.GetAccessibleRepositoriesAsync(Arg.Is<List<int>?>(ids => ids != null && ids.Contains(ProjectId)), Arg.Any<CancellationToken>())
            .Returns([ConfigRepository]);

    private void TheConfigurationRepositoryHolds(string path, string content) =>
        _git.GetBlobAsync(ConfigId, "main", path, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = path, Content = content, Size = content.Length });

    private static PipelineProductionApacheRequest Request(bool monolith = false, string? strategy = null) =>
        new() { ProjectId = ProjectId, Monolith = monolith, Strategy = strategy };

    [Fact]
    public async Task WhenTheExternalSourceCannotBeRead_TheAnalysisStillListsTheFourMissingTemplates()
    {
        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.False(analysis.Readable);
        Assert.True(analysis.ExternalSource);
        Assert.Equal([ApiHttp, ApiHttps, AppHttp, AppHttps], analysis.MissingApacheTemplates);
    }

    [Fact]
    public async Task TheAnalysisOfAnUnreadableSource_DoesNotListATemplateTheConfigurationRepositoryHolds()
    {
        TheConfigurationRepositoryExists();
        TheConfigurationRepositoryHolds(AppHttp, "mine");

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.Equal([ApiHttp, ApiHttps, AppHttps], analysis.MissingApacheTemplates);
    }

    [Fact]
    public async Task TheFullSetup_IsStillRefusedWithoutTheComposeFile_SoTheApacheOnlyPathIsTheWayOut()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyAsync(
            new PipelineProductionApplyRequest { ProjectId = ProjectId, ComposeFile = "deploy/compose.yml" }, Ct));
        await _cli.DidNotReceiveWithAnyArgs().CommitFilesAsync(default!, default!, default!, default!, default!, default!, Ct);
    }

    [Fact]
    public async Task TheFourTemplates_AreCreatedInTheConfigurationRepositoryOfAProjectWhoseExternalSourceCannotBeRead()
    {
        var result = await _sut.ApplyApacheTemplatesAsync(Request(), Ct);

        Assert.True(result.Created);
        Assert.Equal(ConfigSlug, result.Slug);
        Assert.Equal([ApiHttp, ApiHttps, AppHttp, AppHttps], result.FilesAdded.Order(StringComparer.Ordinal));
        Assert.Empty(result.FilesKept);
        var commit = Assert.Single(_commits);
        Assert.Equal($"/repos/{ProjectId}/{ConfigSlug}", commit.DiskPath);
        Assert.Equal([ApiHttp, ApiHttps, AppHttp, AppHttps], commit.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
        Assert.All(commit.Files, file => Assert.Contains("#{", file.Content, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingIsWrittenToTheExternalSourceNorItsMirror()
    {
        await _sut.ApplyApacheTemplatesAsync(Request(), Ct);

        // The only disk path ever resolved or committed to is the configuration repository's own.
        Assert.DoesNotContain(_commits, commit => commit.DiskPath.Contains(MirrorSlug, StringComparison.Ordinal));
        _git.DidNotReceive().ResolveDiskPath(Arg.Any<int>(), MirrorSlug);
        await _git.DidNotReceive().GetBlobAsync(MirrorId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _git.Received(1).CreateConfigurationRepositoryAsync(
            Arg.Is<CreateGitLightRepoRequest>(request => request.ProjectId == ProjectId && request.Name == ConfigSlug), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ATemplateTheConfigurationRepositoryAlreadyHolds_IsKeptAndNeverRewritten()
    {
        TheConfigurationRepositoryExists();
        TheConfigurationRepositoryHolds(AppHttp, "edited by the user");

        var result = await _sut.ApplyApacheTemplatesAsync(Request(), Ct);

        Assert.False(result.Created);
        Assert.Equal([AppHttp], result.FilesKept);
        Assert.Equal([ApiHttp, ApiHttps, AppHttps], result.FilesAdded.Order(StringComparer.Ordinal));
        var commit = Assert.Single(_commits);
        Assert.DoesNotContain(commit.Files, file => file.RelativePath == AppHttp);
        await _git.DidNotReceive().CreateConfigurationRepositoryAsync(Arg.Any<CreateGitLightRepoRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenEveryTemplateIsAlreadyThere_NothingIsCommitted()
    {
        TheConfigurationRepositoryExists();
        foreach (var path in new[] { AppHttp, AppHttps, ApiHttp, ApiHttps })
            TheConfigurationRepositoryHolds(path, "mine");

        var result = await _sut.ApplyApacheTemplatesAsync(Request(), Ct);

        Assert.Empty(result.FilesAdded);
        Assert.Empty(_commits);
    }

    [Fact]
    public async Task AMonolith_GetsTheFrontsTemplatesOnly()
    {
        var result = await _sut.ApplyApacheTemplatesAsync(Request(monolith: true), Ct);

        Assert.Equal([AppHttp, AppHttps], result.FilesAdded.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheStrategyChosen_PicksTheHttpsVirtualHosts()
    {
        await _sut.ApplyApacheTemplatesAsync(Request(strategy: ProductionStrategy.BlueGreen), Ct);

        var https = Assert.Single(_commits).Files.First(file => file.RelativePath == AppHttps).Content;
        Assert.Contains("UPSTREAM_DEFINE", https, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplacement_DoesNotReadTheUpstreamOfABlueGreenCutover()
    {
        await _sut.ApplyApacheTemplatesAsync(Request(strategy: ProductionStrategy.Replace), Ct);

        var https = Assert.Single(_commits).Files.First(file => file.RelativePath == AppHttps).Content;
        Assert.DoesNotContain("UPSTREAM_DEFINE", https, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMonolithIsRefusedTheBlueGreenStrategy_AndNothingIsWritten()
    {
        await Assert.ThrowsAsync<BadRequestException>(
            () => _sut.ApplyApacheTemplatesAsync(Request(monolith: true, strategy: ProductionStrategy.BlueGreen), Ct));

        Assert.Empty(_commits);
        await _git.DidNotReceive().CreateConfigurationRepositoryAsync(Arg.Any<CreateGitLightRepoRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnknownStrategyIsRefused_AndNothingIsWritten()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ApplyApacheTemplatesAsync(Request(strategy: "canary"), Ct));

        Assert.Empty(_commits);
    }
}
