// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Git.Events;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests;

/// <summary>R-76: an organization that asks for it gets a develop branch, cut from the default branch, when a repository
/// of its projects gets its first commit; one that does not, or a repository that must not have one, gets nothing.</summary>
public sealed class GitDevelopBranchProvisionerTests
{
    private const int RepositoryId = 7;
    private const int ProjectId = 5;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IGitLightRepository _repository = Substitute.For<IGitLightRepository>();
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly GitDevelopBranchProvisioner _sut;

    public GitDevelopBranchProvisionerTests()
    {
        _sut = new GitDevelopBranchProvisioner(_repository, _git, NullLogger<GitDevelopBranchProvisioner>.Instance);
        _repository.FindByIdWithProjectAsync(RepositoryId, Arg.Any<CancellationToken>()).Returns(Repository());
        _repository.IsDevelopBranchAutoCreationEnabledAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(true);
        _git.GetBranchesAsync(RepositoryId, Arg.Any<CancellationToken>()).Returns(Branches("main"));
    }

    private static GitInternalRepo Repository(bool configuration = false, GitConnection? connection = null) => new()
    {
        Id = RepositoryId,
        ProjectId = ProjectId,
        Name = "ats",
        Slug = "ats",
        DefaultBranch = "main",
        IsEmpty = false,
        IsConfiguration = configuration,
        GitConnection = connection,
        Project = new Project { Id = ProjectId, Name = "Ats" }
    };

    private static List<GitLightBranchDto> Branches(params string[] names) =>
        [.. names.Select(name => new GitLightBranchDto { Name = name, IsDefault = name == "main" })];

    [Fact]
    public async Task SettingOn_CutsDevelopFromTheDefaultBranch()
    {
        var created = await _sut.EnsureAfterFirstCommitAsync(RepositoryId, Ct);

        Assert.True(created);
        await _git.Received(1).CreateBranchAsync(RepositoryId,
            Arg.Is<CreateGitLightBranchRequest>(request => request.Name == "develop" && request.StartRef == "main"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SettingOff_CreatesNothing_AndDoesNotEvenListTheBranches()
    {
        _repository.IsDevelopBranchAutoCreationEnabledAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(false);

        var created = await _sut.EnsureAfterFirstCommitAsync(RepositoryId, Ct);

        Assert.False(created);
        await _git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default, default!, Ct);
        await _git.DidNotReceiveWithAnyArgs().GetBranchesAsync(default, Ct);
    }

    [Fact]
    public async Task ADevelopBranchThatExists_IsLeftAlone()
    {
        _git.GetBranchesAsync(RepositoryId, Arg.Any<CancellationToken>()).Returns(Branches("main", "develop"));

        Assert.False(await _sut.EnsureAfterFirstCommitAsync(RepositoryId, Ct));
        await _git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default, default!, Ct);
    }

    [Fact]
    public async Task WithoutACommitOnTheDefaultBranch_ThereIsNothingToCutFrom()
    {
        _git.GetBranchesAsync(RepositoryId, Arg.Any<CancellationToken>()).Returns(Branches("feature"));

        Assert.False(await _sut.EnsureAfterFirstCommitAsync(RepositoryId, Ct));
        await _git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default, default!, Ct);
    }

    [Fact]
    public async Task TheConfigurationRepository_GetsNoDevelopBranch()
    {
        _repository.FindByIdWithProjectAsync(RepositoryId, Arg.Any<CancellationToken>()).Returns(Repository(configuration: true));

        Assert.False(await _sut.EnsureAfterFirstCommitAsync(RepositoryId, Ct));
        await _git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default, default!, Ct);
    }

    [Fact]
    public async Task TheMirrorOfAnExternalRepository_GetsNoDevelopBranch()
    {
        var mirror = new GitConnection { Id = 99, ProviderType = GitProviderType.GitHub };
        _repository.FindByIdWithProjectAsync(RepositoryId, Arg.Any<CancellationToken>()).Returns(Repository(connection: mirror));

        Assert.False(await _sut.EnsureAfterFirstCommitAsync(RepositoryId, Ct));
        await _git.DidNotReceiveWithAnyArgs().CreateBranchAsync(default, default!, Ct);
    }

    [Fact]
    public async Task AnUnknownRepository_CreatesNothing()
    {
        _repository.FindByIdWithProjectAsync(404, Arg.Any<CancellationToken>()).Returns((GitInternalRepo?)null);

        Assert.False(await _sut.EnsureAfterFirstCommitAsync(404, Ct));
    }

    [Fact]
    public async Task AFailingBranchCreation_NeverFailsTheCommitOrPushThatAskedForIt()
    {
        _git.CreateBranchAsync(RepositoryId, Arg.Any<CreateGitLightBranchRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git failed"));

        Assert.False(await _sut.EnsureAfterFirstCommitAsync(RepositoryId, Ct));
    }

    [Fact]
    public async Task APushThatCreatesTheDefaultBranch_AsksForTheDevelopBranch()
    {
        var provisioner = Substitute.For<IGitDevelopBranchProvisioner>();
        var handler = new GitDevelopBranchPushHandler(provisioner);

        await handler.HandleAsync(Push("main", new GitRefUpdate("refs/heads/main", new string('a', 40), new string('0', 40))), Ct);

        await provisioner.Received(1).EnsureAfterFirstCommitAsync(RepositoryId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("refs/heads/main", "main", "b")]
    [InlineData("refs/heads/feature", "main", "0")]
    [InlineData("refs/tags/v1", "main", "0")]
    public async Task APushThatDoesNotCreateTheDefaultBranch_AsksForNothing(string reference, string defaultBranch, string oldIdCharacter)
    {
        var provisioner = Substitute.For<IGitDevelopBranchProvisioner>();
        var handler = new GitDevelopBranchPushHandler(provisioner);

        await handler.HandleAsync(
            Push(defaultBranch, new GitRefUpdate(reference, new string('a', 40), new string(oldIdCharacter[0], 40))), Ct);

        await provisioner.DidNotReceiveWithAnyArgs().EnsureAfterFirstCommitAsync(default, Ct);
    }

    private static GitPushProcessedEvent Push(string defaultBranch, GitRefUpdate update) =>
        new(ProjectId, RepositoryId, "ats", "ats", defaultBranch, "/repos/ats", [update]);
}
