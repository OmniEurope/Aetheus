// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.PortRegistry;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Projects;

/// <summary> The orchestration of a project deletion. The removal itself is proved on PostgreSQL by
/// ProjectDeletionIntegrationTests; this covers the refusal, the preview shape, the files removed
/// after the commit and what the audit entry says. </summary>
public sealed class ProjectDeletionServiceTests
{
    private static readonly ProjectDeletionScope Scope = new(
        7, "Portfolio", 3, [11], [12], [13, 14], [15], [16], [17], [18], [19], [20]);

    private readonly IProjectDeletionRepository _repo = Substitute.For<IProjectDeletionRepository>();
    private readonly IDbTransactionScope _transaction = Substitute.For<IDbTransactionScope>();
    private readonly IArtifactStorageService _storage = Substitute.For<IArtifactStorageService>();
    private readonly IGitLightService _gitLight = Substitute.For<IGitLightService>();
    private readonly IGitLightCliService _gitCli = Substitute.For<IGitLightCliService>();
    private readonly IPortRegistryService _ports = Substitute.For<IPortRegistryService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly ProjectDeletionService _sut;

    public ProjectDeletionServiceTests()
    {
        _transaction.IsRelational.Returns(true);
        _repo.ResolveScopeAsync(7, Arg.Any<CancellationToken>()).Returns(Scope);
        _repo.CountAsync(Scope, Arg.Any<CancellationToken>()).Returns(new Dictionary<ProjectDeletionItemKind, int>
        {
            [ProjectDeletionItemKind.Vaults] = 3,
            [ProjectDeletionItemKind.Pipelines] = 2,
            [ProjectDeletionItemKind.WorkItems] = 0,
        });
        _repo.GetArtifactFilePathsAsync(Scope, Arg.Any<CancellationToken>()).Returns(["7/13/1/a.zip", "7/13/1/b.zip"]);
        _repo.GetGitRepositorySlugsAsync(Scope, Arg.Any<CancellationToken>()).Returns(["portfolio"]);
        _gitLight.ResolveDiskPath(7, "portfolio").Returns("/repos/7/portfolio.git");
        _sut = new ProjectDeletionService(_repo, _transaction, _storage, _gitLight, _gitCli, _ports, _audit,
            Substitute.For<IEntityChangeNotifier>(), new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ProjectDeletionService>.Instance);
    }

    [Fact]
    public async Task GetPreviewAsync_ListsTheNonEmptyKindsInEnumOrder_WithTheActiveRunCount()
    {
        _repo.CountActiveRunsAsync(Scope, Arg.Any<CancellationToken>()).Returns(1);

        var preview = await _sut.GetPreviewAsync(7, TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.Equal("Portfolio", preview.ProjectName);
        Assert.Equal(1, preview.ActiveRunCount);
        Assert.Equal(
            [new ProjectDeletionItemDto(ProjectDeletionItemKind.Pipelines, 2), new ProjectDeletionItemDto(ProjectDeletionItemKind.Vaults, 3)],
            preview.Items);
    }

    [Fact]
    public async Task DeleteAsync_WithARunInProgress_IsRefusedBeforeAnythingIsRemoved()
    {
        _repo.CountActiveRunsAsync(Scope, Arg.Any<CancellationToken>()).Returns(2);

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => _sut.DeleteAsync(7, TestContext.Current.CancellationToken));

        Assert.Contains("2 pipeline run(s) in progress", refusal.Message, StringComparison.Ordinal);
        await _repo.DidNotReceiveWithAnyArgs().DeleteAsync(default!, TestContext.Current.CancellationToken);
        await _ports.DidNotReceiveWithAnyArgs().ReleaseProjectPortsAsync(default, TestContext.Current.CancellationToken);
        await _storage.DidNotReceiveWithAnyArgs().DeleteArtifactAsync(default!, TestContext.Current.CancellationToken);
        await _transaction.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRowsInTheTransaction_ThenTheFiles_AndAuditsTheCounts()
    {
        _storage.DeleteArtifactAsync("7/13/1/b.zip", Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("locked"));

        Assert.True(await _sut.DeleteAsync(7, TestContext.Current.CancellationToken));

        Received.InOrder(() =>
        {
            _transaction.BeginTransactionAsync(Arg.Any<CancellationToken>());
            _ports.ReleaseProjectPortsAsync(7, Arg.Any<CancellationToken>());
            _repo.DeleteAsync(Scope, Arg.Any<CancellationToken>());
            _transaction.CommitAsync(Arg.Any<CancellationToken>());
            _storage.DeleteArtifactAsync("7/13/1/a.zip", Arg.Any<CancellationToken>());
            _gitCli.DeleteRepoAsync("/repos/7/portfolio.git", Arg.Any<CancellationToken>());
        });
        await _audit.Received(1).LogAsync("Deleted", "Project", 7,
            "Portfolio; removed: Pipelines=2, Vaults=3; artifact files left on disk: 1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_OfAMissingProject_ReturnsFalseAndDeletesNothing()
    {
        Assert.False(await _sut.DeleteAsync(99, TestContext.Current.CancellationToken));
        await _repo.DidNotReceiveWithAnyArgs().DeleteAsync(default!, TestContext.Current.CancellationToken);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, TestContext.Current.CancellationToken);
    }
}
