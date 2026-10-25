// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>ADR-057: the configuration repository of a project is a setting that resolves to one repository at a
/// time, the wizard's or the one the user designated since, never a single repository hardwired by name.</summary>
public sealed class ProjectConfigRepositoryServiceTests
{
    private const string EnvSample = ".pipeline/configs/env/production.env";
    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly IGitCommitRecorder _recorder = Substitute.For<IGitCommitRecorder>();
    private readonly IPipelineGitService _pipelineGit = Substitute.For<IPipelineGitService>();
    private readonly ProjectConfigRepositoryService _sut;

    public ProjectConfigRepositoryServiceTests()
    {
        _git.ResolveDiskPath(Arg.Any<int>(), Arg.Any<string>()).Returns(call => $"/repos/{call.ArgAt<int>(0)}/{call.ArgAt<string>(1)}");
        _cli.CommitFilesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(string RelativePath, string Content)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));
        _sut = new ProjectConfigRepositoryService(
            _git, _cli, _recorder, _pipelineGit, Substitute.For<IPipelineRepository>(),
            Substitute.For<ILogger<ProjectConfigRepositoryService>>());
    }

    private static GitLightRepoDto Repo(int id, string slug, bool configuration) => new()
    {
        Id = id,
        ProjectId = 7,
        Name = slug,
        Slug = slug,
        DefaultBranch = "main",
        IsConfiguration = configuration
    };

    private void Repositories(params GitLightRepoDto[] repositories) =>
        _git.GetAccessibleRepositoriesAsync(Arg.Is<List<int>?>(ids => ids != null && ids.Contains(7)), Arg.Any<CancellationToken>())
            .Returns([.. repositories]);

    private void Blob(int repositoryId, string content) =>
        _git.GetBlobAsync(repositoryId, "main", EnvSample, Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Path = EnvSample, Content = content, Size = content.Length });

    [Fact]
    public async Task TheConfigurationRepository_IsTheFlaggedOne_NotTheOneNamedAfterTheProject()
    {
        Repositories(Repo(1, "app", false), Repo(2, "app-config", false), Repo(3, "our-hosting-files", true));

        var found = await _sut.FindAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal(3, found!.Id);
    }

    [Fact]
    public async Task WithNoFlaggedRepository_TheProjectHasNone_AndANamedLookAlikeIsNotTaken()
    {
        Repositories(Repo(1, "app", false), Repo(2, "app-config", false));

        Assert.Null(await _sut.FindAsync(7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenTheUserDesignatesAnotherRepository_TheFilesAreReadFromIt_NotFromTheWizardsOne()
    {
        Blob(2, "FROM_WIZARD=#{A}#");
        Blob(3, "FROM_USER=#{A}#");

        Repositories(Repo(1, "app", false), Repo(2, "app-config", true));
        Assert.Equal("FROM_WIZARD=#{A}#", await _sut.ReadAsync(7, EnvSample, null, null, TestContext.Current.CancellationToken));

        // The user designated their own repository: the wizard's one is no longer flagged.
        Repositories(Repo(1, "app", false), Repo(2, "app-config", false), Repo(3, "our-hosting-files", true));
        Assert.Equal("FROM_USER=#{A}#", await _sut.ReadAsync(7, EnvSample, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenTheDesignatedRepositoryLacksTheFile_TheProjectsOwnSourceAtTheRunsRevisionIsRead()
    {
        const string revision = "0123456789abcdef0123456789abcdef01234567";
        Repositories(Repo(1, "app", false), Repo(3, "our-hosting-files", true));
        _pipelineGit.ReadProjectConfigAtRevisionAsync(7, EnvSample, revision, Arg.Any<CancellationToken>(), 1)
            .Returns("FROM_SOURCE=#{A}#");

        var content = await _sut.ReadAsync(7, EnvSample, revision, 1, TestContext.Current.CancellationToken);

        Assert.Equal("FROM_SOURCE=#{A}#", content);
    }

    [Fact]
    public async Task TheGeneratedFiles_AreCommittedToTheDesignatedRepository_AndNoWizardRepositoryIsCreatedBesideIt()
    {
        Repositories(Repo(1, "app", false), Repo(3, "our-hosting-files", true));

        var result = await _sut.EnsureAsync(
            7, new Dictionary<string, string> { [EnvSample] = "A=#{A}#" }, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Id);
        Assert.False(result.Created);
        Assert.Equal([EnvSample], result.FilesAdded);
        await _git.DidNotReceive().CreateConfigurationRepositoryAsync(
            Arg.Any<CreateGitLightRepoRequest>(), Arg.Any<CancellationToken>());
        await _cli.Received(1).CommitFilesAsync(
            "/repos/7/our-hosting-files", "main", Arg.Any<IReadOnlyList<(string RelativePath, string Content)>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Replacing_CommitsTheNewContentOfAFileTheConfigurationRepositoryHolds_AndNothingElse()
    {
        Repositories(Repo(1, "app", false), Repo(3, "our-hosting-files", true));
        Blob(3, "ENV=#{ENV}#\n");

        var replaced = await _sut.ReplaceAsync(7, EnvSample, "ENV=#{DEPLOY_ENV}#\n", "fix(config): mend", TestContext.Current.CancellationToken);

        Assert.True(replaced);
        await _cli.Received(1).CommitFilesAsync(
            "/repos/7/our-hosting-files", "main",
            Arg.Is<IReadOnlyList<(string RelativePath, string Content)>>(files =>
                files.Count == 1 && files[0].RelativePath == EnvSample && files[0].Content == "ENV=#{DEPLOY_ENV}#\n"),
            "fix(config): mend", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _recorder.Received(1).RecordCommitAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Replacing_WithTheContentTheFileAlreadyHolds_CommitsNothing()
    {
        Repositories(Repo(3, "our-hosting-files", true));
        Blob(3, "ENV=#{DEPLOY_ENV}#\n");

        var replaced = await _sut.ReplaceAsync(7, EnvSample, "ENV=#{DEPLOY_ENV}#\n", "fix(config): mend", TestContext.Current.CancellationToken);

        Assert.False(replaced);
        await _cli.DidNotReceiveWithAnyArgs().CommitFilesAsync(
            default!, default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replacing_AFileTheRepositoryDoesNotHold_CreatesNothing()
    {
        Repositories(Repo(3, "our-hosting-files", true));

        var replaced = await _sut.ReplaceAsync(7, EnvSample, "ENV=#{DEPLOY_ENV}#\n", "fix(config): mend", TestContext.Current.CancellationToken);

        Assert.False(replaced);
        await _cli.DidNotReceiveWithAnyArgs().CommitFilesAsync(
            default!, default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replacing_WithNoConfigurationRepository_IsRefused_AndNeverFallsBackToTheSource()
    {
        Repositories(Repo(1, "app", false));

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.NotFoundException>(() =>
            _sut.ReplaceAsync(7, EnvSample, "x", "msg", TestContext.Current.CancellationToken));
        await _cli.DidNotReceiveWithAnyArgs().CommitFilesAsync(
            default!, default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData(".pipeline/configs/../../outside.env")]
    [InlineData("src/Program.cs")]
    public async Task Replacing_APathOutsideTheConfigurationFiles_IsRefused(string path)
    {
        Repositories(Repo(3, "our-hosting-files", true));

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() =>
            _sut.ReplaceAsync(7, path, "x", "msg", TestContext.Current.CancellationToken));
        await _cli.DidNotReceiveWithAnyArgs().CommitFilesAsync(
            default!, default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFileTheDesignatedRepositoryAlreadyHolds_IsKept_NeverOverwritten()
    {
        Repositories(Repo(3, "our-hosting-files", true));
        Blob(3, "EDITED_BY_THE_USER=#{A}#");

        var result = await _sut.EnsureAsync(
            7, new Dictionary<string, string> { [EnvSample] = "A=#{A}#" }, TestContext.Current.CancellationToken);

        Assert.Equal([EnvSample], result.FilesKept);
        Assert.Empty(result.FilesAdded);
        await _cli.DidNotReceive().CommitFilesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(string RelativePath, string Content)>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
