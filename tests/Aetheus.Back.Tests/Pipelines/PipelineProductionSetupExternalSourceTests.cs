// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.Pipelines;
using Aetheus.Shared.Components.VariableLibraries;
using Aetheus.Shared.Components.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Recette R-69 (OE2): the project's source is an external Git repository, read through its internal mirror. The
/// production analysis reads the mirror like any repository; the two states in which it cannot (the external repository sits
/// beside an internal repository that holds no commit, or its mirror was never synchronized) are named, so the wizard can
/// refuse to create a production or QA pipeline that could never deploy. Every repository here is a double of the Git service,
/// as in the other production setup tests: the real database rows are covered by the integration tests of the same recette.</summary>
public sealed class PipelineProductionSetupExternalSourceTests
{
    private const int ProjectId = 17;
    private const int InternalId = 4;
    private const int MirrorId = 5;
    private static readonly string ComposePath = PipelineProductionSetupServiceTests.ComposePath;

    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly PipelineProductionSetupService _sut;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PipelineProductionSetupExternalSourceTests()
    {
        var pipelines = Substitute.For<IPipelineRepository>();
        var libraries = Substitute.For<IVariableLibraryService>();
        var vaults = Substitute.For<IVaultService>();
        _sut = new PipelineProductionSetupService(
            pipelines, _git, libraries, vaults, Substitute.For<IPortAllocationService>(),
            Substitute.For<IProjectConfigRepositoryService>(), Substitute.For<IAuditService>(),
            Substitute.For<IPipelineSetupServerRepository>(), NullLogger<PipelineProductionSetupService>.Instance);
        pipelines.GetProjectNameAsync(ProjectId, Arg.Any<CancellationToken>()).Returns("OE2");
        libraries.GetLibrariesAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VariableLibraryDto> { Items = [] });
        vaults.GetVaultsAsync(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<PaginationRequest?>(), Arg.Any<List<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<VaultDto> { Items = [] });
        // The mirror holds the Compose file on main, the internal repository holds nothing.
        _git.GetTreeAsync(MirrorId, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => TreeOf((string?)call.ArgAt<string?>(2)));
        _git.GetBlobAsync(MirrorId, "main", ComposePath, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = ComposePath, Content = PipelineProductionSetupServiceTests.W6Compose });
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
            new GitLightTreeEntryDto { Name = name, Path = prefix + name, Type = slash < 0 ? GitTreeEntryType.Blob : GitTreeEntryType.Tree }
        ];
    }

    private void Repositories(params GitLightRepoDto[] repositories) =>
        _git.GetRepositoriesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([.. repositories]);

    private static GitLightRepoDto Internal(bool empty) =>
        new() { Id = InternalId, ProjectId = ProjectId, Name = "oe2", Slug = "oe2", DefaultBranch = "main", IsEmpty = empty };

    private static GitLightRepoDto Mirror(bool empty, bool additional) =>
        new()
        {
            Id = MirrorId,
            ProjectId = ProjectId,
            Name = "OmniEurope.Blazor",
            Slug = "omnieurope-blazor",
            DefaultBranch = "main",
            IsEmpty = empty,
            IsExternalMirror = true,
            IsAdditionalSource = additional
        };

    [Fact]
    public async Task TheMirrorOfTheExternalSource_IsReadLikeAnyRepository_AndItsComposeFileIsFound()
    {
        Repositories(Mirror(empty: false, additional: false));

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.True(analysis.Readable);
        Assert.True(analysis.ExternalSource);
        Assert.Equal(ProductionSourceProblem.None, analysis.SourceProblem);
        Assert.Equal(ComposePath, analysis.ComposeFile);
    }

    [Fact]
    public async Task AnExternalRepositoryBesideAnEmptyInternalOne_IsNotReadable_AndSaysItIsNotTheSource()
    {
        // The state OE2 was left in: the wizard created an empty internal repository, then GitHub was attached beside it. The
        // mirror holds the code but is only an additional source, so the project's source is the empty repository.
        Repositories(Internal(empty: true), Mirror(empty: false, additional: true));

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.False(analysis.Readable);
        Assert.True(analysis.ExternalSource);
        Assert.Equal(ProductionSourceProblem.ExternalNotTheSource, analysis.SourceProblem);
    }

    [Fact]
    public async Task AnExternalSourceWhoseMirrorWasNeverSynchronized_IsNotReadable_AndSaysSo()
    {
        Repositories(Mirror(empty: true, additional: false));

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.False(analysis.Readable);
        Assert.True(analysis.ExternalSource);
        Assert.Equal(ProductionSourceProblem.ExternalNotSynchronized, analysis.SourceProblem);
    }

    [Fact]
    public async Task AnInternalProjectWithoutCommit_IsNotAnExternalSource_SoRecetteR80KeepsItsBehavior()
    {
        Repositories(Internal(empty: true));

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.False(analysis.Readable);
        Assert.False(analysis.ExternalSource);
        Assert.Equal(ProductionSourceProblem.None, analysis.SourceProblem);
    }

    [Fact]
    public async Task AnExternalRepositoryBesideAnInternalOneThatHoldsCode_LeavesTheInternalOneTheSource()
    {
        Repositories(Internal(empty: false), Mirror(empty: false, additional: true));
        _git.GetTreeAsync(InternalId, "main", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);

        var analysis = await _sut.AnalyzeAsync(ProjectId, null, Ct);

        Assert.True(analysis.Readable);
        Assert.False(analysis.ExternalSource);
        Assert.Equal(ProductionSourceProblem.None, analysis.SourceProblem);
    }
}
