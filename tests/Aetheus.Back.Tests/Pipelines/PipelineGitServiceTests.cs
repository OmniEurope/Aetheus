// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services.Email;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineGitServiceTests
{
    private readonly IGitLightService _gitServiceMock = Substitute.For<IGitLightService>();
    private readonly IGitLightCliService _cliMock = Substitute.For<IGitLightCliService>();
    private readonly IPipelineRepository _pipelineRepoMock = Substitute.For<IPipelineRepository>();
    private readonly IGitCommitRecorder _commitRecorderMock = Substitute.For<IGitCommitRecorder>();
    private readonly IEmailSenderConfigurationResolver _senderResolverMock = Substitute.For<IEmailSenderConfigurationResolver>();
    private readonly PipelineGitService _sut;

    public PipelineGitServiceTests()
    {
        _senderResolverMock.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new EmailSenderResolution());
        _sut = new PipelineGitService(
            _gitServiceMock,
            _cliMock,
            _pipelineRepoMock,
            _commitRecorderMock,
            _senderResolverMock,
            NullLogger<PipelineGitService>.Instance);
    }

    // --- ReadProjectPipelineYamlAsync ---

    [Fact]
    public async Task ReadProjectPipelineYamlAsync_NoRepo_ReturnsNull()
    {
        _gitServiceMock.GetRepositoriesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<GitLightRepoDto>());

        var result = await _sut.ReadProjectPipelineYamlAsync(1, "deploy", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadProjectPipelineYamlAsync_EmptyRepo_ReturnsNull()
    {
        _gitServiceMock.GetRepositoriesAsync(1, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = 1, ProjectId = 1, Slug = "my-repo", IsEmpty = true }]);

        var result = await _sut.ReadProjectPipelineYamlAsync(1, "deploy", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadProjectPipelineYamlAsync_DirectBlobMatch_ReturnsYaml()
    {
        var yaml = "name: deploy\ntrigger: manual\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build";
        SetupRepoWithDiskPath(1, "my-repo", "main");

        _cliMock.GetBlobAsync(Arg.Any<string>(), "main", ".pipeline/deploy.yaml", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Content = yaml, Path = ".pipeline/deploy.yaml" });

        var result = await _sut.ReadProjectPipelineYamlAsync(1, "deploy", ct: TestContext.Current.CancellationToken);

        Assert.Equal(yaml, result);
    }

    [Fact]
    public async Task ReadProjectPipelineYamlAsync_NoDirectBlob_FallsBackToTreeSearch()
    {
        var yaml = "name: my-pipeline\ntrigger: manual\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build";
        SetupRepoWithDiskPath(1, "my-repo", "main");

        // Direct lookup returns nothing
        _cliMock.GetBlobAsync(Arg.Any<string>(), "main", ".pipeline/my-pipeline.yaml", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Content = null, Path = ".pipeline/my-pipeline.yaml" });

        // Tree has a different filename
        _cliMock.GetTreeAsync(Arg.Any<string>(), "main", ".pipeline", Arg.Any<CancellationToken>())
            .Returns([new GitLightTreeEntryDto { Name = "other.yaml", Type = GitTreeEntryType.Blob }]);

        _cliMock.GetBlobAsync(Arg.Any<string>(), "main", ".pipeline/other.yaml", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Content = yaml, Path = ".pipeline/other.yaml" });

        var result = await _sut.ReadProjectPipelineYamlAsync(1, "my-pipeline", ct: TestContext.Current.CancellationToken);

        Assert.Equal(yaml, result);
    }

    [Fact]
    public async Task ReadProjectPipelineYamlAsync_UsesDefaultBranch()
    {
        SetupRepoWithDiskPath(1, "my-repo", "develop");

        _cliMock.GetBlobAsync(Arg.Any<string>(), "develop", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Content = null });

        _cliMock.GetTreeAsync(Arg.Any<string>(), "develop", ".pipeline", Arg.Any<CancellationToken>())
            .Returns(new List<GitLightTreeEntryDto>());

        var result = await _sut.ReadProjectPipelineYamlAsync(1, "deploy", ct: TestContext.Current.CancellationToken);

        // Verify it used the repo's default branch
        await _cliMock.Received().GetBlobAsync(Arg.Any<string>(), "develop", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadProjectPipelineYamlAtRevisionAsync_UsesExactCommit()
    {
        const string commit = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        SetupRepoWithDiskPath(1, "my-repo", "develop");
        var yaml = "name: deploy\ntrigger: manual\nstages: []";
        _cliMock.GetBlobAsync(Arg.Any<string>(), commit, ".pipeline/deploy.yaml", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Content = yaml, Path = ".pipeline/deploy.yaml" });

        var result = await _sut.ReadProjectPipelineYamlAtRevisionAsync(1, "deploy", commit, ct: TestContext.Current.CancellationToken);

        Assert.Equal(yaml, result);
        await _cliMock.Received().GetBlobAsync(
            Arg.Any<string>(), commit, ".pipeline/deploy.yaml", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R534_AnAdditionalSource_IsNeverWhereADefinitionIsReadByDefault()
    {
        // The project's repository plus the mirror of an external one attached beside it: a
        // pipeline bound to no repository still reads its definition from the project's own. the
        // source is read from the stored repositories (no default-branch sync).
        _gitServiceMock.GetAccessibleRepositoriesAsync(Arg.Is<List<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 1 })), Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightRepoDto { Id = 10, ProjectId = 1, Slug = "aetheus", DefaultBranch = "develop" },
            new GitLightRepoDto { Id = 20, ProjectId = 1, Slug = "aetheus-public", DefaultBranch = "main", IsAdditionalSource = true }
        ]);
        var tempDir = Directory.CreateTempSubdirectory("pgs-test-r534-").FullName;
        _gitServiceMock.ResolveDiskPath(1, "aetheus").Returns(tempDir);
        _cliMock.GetCommitsAsync(tempDir, "develop", skip: 0, take: 1, ct: Arg.Any<CancellationToken>())
            .Returns([new GitLightCommitDto { Sha = "c755999fc0" }]);

        var source = await _sut.GetPipelineSourceAsync(1, "deploy", TestContext.Current.CancellationToken);

        Assert.NotNull(source);
        Assert.Equal((10, "develop", "c755999fc0"), (source.RepositoryId, source.Branch, source.CommitHash));
    }

    [Fact]
    public async Task ReadProjectPipelineYamlAsync_MultipleRepositoriesWithoutSelection_IsRejected()
    {
        _gitServiceMock.GetRepositoriesAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightRepoDto { Id = 10, ProjectId = 1, Slug = "first", DefaultBranch = "main" },
            new GitLightRepoDto { Id = 20, ProjectId = 1, Slug = "second", DefaultBranch = "main" }
        ]);

        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ReadProjectPipelineYamlAsync(
                1, "deploy", ct: TestContext.Current.CancellationToken));

        Assert.Contains("multiple repositories", error.Message, StringComparison.OrdinalIgnoreCase);
        await _cliMock.DidNotReceiveWithAnyArgs().GetBlobAsync(
            string.Empty, string.Empty, string.Empty, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadProjectConfigAtRevisionAsync_SelectedRepository_UsesExactCommitAndSafePath()
    {
        const string commit = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        _gitServiceMock.GetRepositoriesAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightRepoDto { Id = 10, ProjectId = 1, Slug = "first", DefaultBranch = "main" },
            new GitLightRepoDto { Id = 20, ProjectId = 1, Slug = "second", DefaultBranch = "develop" }
        ]);
        var secondPath = Path.Combine(Path.GetTempPath(), "pgs-test-1-second");
        Directory.CreateDirectory(secondPath);
        _gitServiceMock.ResolveDiskPath(1, "second").Returns(secondPath);
        _cliMock.GetBlobAsync(
                secondPath, commit, ".pipeline/configs/apache/site.conf", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto
            {
                Content = "ServerName #{HOST}#\n",
                Path = ".pipeline/configs/apache/site.conf"
            });

        var result = await _sut.ReadProjectConfigAtRevisionAsync(
            1, ".pipeline/configs/apache/site.conf", commit,
            TestContext.Current.CancellationToken, 20);

        Assert.Equal("ServerName #{HOST}#\n", result);
        await _cliMock.Received(1).GetBlobAsync(
            secondPath, commit, ".pipeline/configs/apache/site.conf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadVersionPropsAtRevisionAsync_ReadsTheRootPropsAtTheExactCommit()
    {
        const string commit = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        _gitServiceMock.GetRepositoriesAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightRepoDto { Id = 10, ProjectId = 1, Slug = "first", DefaultBranch = "main" }
        ]);
        var path = Path.Combine(Path.GetTempPath(), "pgs-test-1-props");
        Directory.CreateDirectory(path);
        _gitServiceMock.ResolveDiskPath(1, "first").Returns(path);
        _cliMock.GetBlobAsync(path, commit, "Directory.Build.props", Arg.Any<CancellationToken>())
            .Returns(new GitLightBlobDto { Content = "<VersionPrefix>1.4.0</VersionPrefix>", Path = "Directory.Build.props" });

        var props = await _sut.ReadVersionPropsAtRevisionAsync(1, commit, TestContext.Current.CancellationToken);

        Assert.Equal("1.4", ReleaseSeries.FromVersionProps(props));
    }

    [Fact]
    public async Task ReadVersionPropsAtRevisionAsync_AMutableRevision_ReadsNothing()
    {
        var props = await _sut.ReadVersionPropsAtRevisionAsync(1, "main", TestContext.Current.CancellationToken);

        Assert.Null(props);
        await _gitServiceMock.DidNotReceiveWithAnyArgs().GetRepositoriesAsync(
            default, TestContext.Current.CancellationToken);
    }
    [Theory]
    [InlineData(".pipeline/configs/../secret.conf", null)]
    [InlineData(".pipeline/configs/apache/site.conf", "main")]
    public async Task ReadProjectConfigAtRevisionAsync_UnsafePathOrMutableRevision_IsRejected(
        string path, string? revision)
    {
        revision ??= new string('a', 40);
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ReadProjectConfigAtRevisionAsync(
                1, path, revision, TestContext.Current.CancellationToken));

        await _gitServiceMock.DidNotReceiveWithAnyArgs().GetRepositoriesAsync(
            default, TestContext.Current.CancellationToken);
    }

    // --- WriteProjectPipelineYamlAsync ---

    [Fact]
    public async Task WriteProjectPipelineYamlAsync_NoRepo_ReturnsNoRepo()
    {
        _gitServiceMock.GetRepositoriesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<GitLightRepoDto>());

        var (outcome, error) = await _sut.WriteProjectPipelineYamlAsync(1, "deploy", "yaml content", "admin", ct: TestContext.Current.CancellationToken);

        // No internal repo is a non-error DB-only fallback, not a git failure.
        Assert.Equal(GitWriteOutcome.NoRepo, outcome);
        Assert.Null(error);
    }

    [Fact]
    public async Task WriteProjectPipelineYamlAsync_ValidRepo_CommitsFile()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");

        _cliMock.CommitFileAsync(
            Arg.Any<string>(), "main", ".pipeline/deploy.yaml", "yaml content",
            Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));

        var (outcome, error) = await _sut.WriteProjectPipelineYamlAsync(1, "deploy", "yaml content", "admin", ct: TestContext.Current.CancellationToken);

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        Assert.Null(error);
        // An Aetheus-made commit clears the repository's empty flag.
        await _commitRecorderMock.Received(1).RecordCommitAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteProjectPipelineYamlAsync_SelectedBranch_CommitsToSelectedBranch()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _cliMock.CommitFileAsync(
            Arg.Any<string>(), "develop", ".pipeline/deploy.yaml", "yaml content",
            Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));

        var (outcome, _) = await _sut.WriteProjectPipelineYamlAsync(
            1, "deploy", "yaml content", "admin", sourceBranch: "develop", ct: TestContext.Current.CancellationToken);

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        await _cliMock.Received(1).CommitFileAsync(
            Arg.Any<string>(), "develop", ".pipeline/deploy.yaml", "yaml content",
            Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteProjectPipelineYamlAsync_CommitFails_ReturnsFailed()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");

        _cliMock.CommitFileAsync(
            Arg.Any<string>(), "main", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, (string?)null, "conflict"));

        var (outcome, error) = await _sut.WriteProjectPipelineYamlAsync(1, "deploy", "yaml", "admin", ct: TestContext.Current.CancellationToken);

        Assert.Equal(GitWriteOutcome.Failed, outcome);
        Assert.Equal("conflict", error);
        await _commitRecorderMock.DidNotReceive().RecordCommitAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyProjectPipelineChangeAsync_SameRepoRename_IsOneAtomicCommit()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _cliMock.CommitFileChangesAsync(
                Arg.Any<string>(), "main",
                Arg.Any<IReadOnlyList<(string RelativePath, string Content)>>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail,
                Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));

        var (outcome, error) = await _sut.ApplyProjectPipelineChangeAsync(
            new PipelineGitDefinitionLocation(1, "old name", "main"),
            new PipelineGitDefinitionLocation(1, "new name", "main"),
            "name: new name", "admin", ct: TestContext.Current.CancellationToken);

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        Assert.Null(error);
        await _cliMock.Received(1).CommitFileChangesAsync(
            Arg.Any<string>(), "main",
            Arg.Is<IReadOnlyList<(string RelativePath, string Content)>>(files =>
                files.Count == 1 && files[0].RelativePath == ".pipeline/new-name.yaml"),
            Arg.Is<IReadOnlyList<string>>(paths =>
                paths.Count == 1 && paths[0] == ".pipeline/old-name.yaml"),
            Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyProjectPipelineChangeAsync_Delete_CommitsYamlRemoval()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _cliMock.CommitFileChangesAsync(
                Arg.Any<string>(), "main", Arg.Any<IReadOnlyList<(string, string)>>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail,
                Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));

        var (outcome, _) = await _sut.ApplyProjectPipelineChangeAsync(
            new PipelineGitDefinitionLocation(1, "obsolete", "main"), null, null, "admin", ct: TestContext.Current.CancellationToken);

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        await _cliMock.Received(1).CommitFileChangesAsync(
            Arg.Any<string>(), "main", Arg.Is<IReadOnlyList<(string, string)>>(files => files.Count == 0),
            Arg.Is<IReadOnlyList<string>>(paths =>
                paths.Count == 1 && paths[0] == ".pipeline/obsolete.yaml"),
            Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, Arg.Any<CancellationToken>());
    }

    // --- CopyEnvironmentPipelinesToProjectAsync ---

    [Fact]
    public async Task CopyEnvironmentPipelinesToProjectAsync_NoRepo_ReturnsZero()
    {
        _gitServiceMock.GetRepositoriesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<GitLightRepoDto>());

        var result = await _sut.CopyEnvironmentPipelinesToProjectAsync(1, "staging", 1, "admin", ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task CopyEnvironmentPipelinesToProjectAsync_NoPipelines_ReturnsZero()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _pipelineRepoMock.GetPipelinesByEnvironmentAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<Pipeline>());

        var result = await _sut.CopyEnvironmentPipelinesToProjectAsync(5, "staging", 1, "admin", ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task CopyEnvironmentPipelinesToProjectAsync_WithPipelines_CommitsAndReturnsCount()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _pipelineRepoMock.GetPipelinesByEnvironmentAsync(5, Arg.Any<CancellationToken>())
            .Returns([
                new Pipeline { Id = 1, Name = "build", YamlDefinition = "steps: []" },
                new Pipeline { Id = 2, Name = "deploy", YamlDefinition = "steps: []" }
            ]);
        _cliMock.CommitFilesAsync(
            Arg.Any<string>(), "main", Arg.Any<IReadOnlyList<(string, string)>>(),
            Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));

        var result = await _sut.CopyEnvironmentPipelinesToProjectAsync(5, "staging", 1, "admin", ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result);
        await _commitRecorderMock.Received(1).RecordCommitAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_21_CopyByTheBackgroundHandler_IsSignedByAetheus_NotBySystem()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _pipelineRepoMock.GetPipelinesByEnvironmentAsync(5, Arg.Any<CancellationToken>())
            .Returns([new Pipeline { Id = 1, Name = "build", YamlDefinition = "steps: []" }]);
        _cliMock.CommitFilesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));

        await _sut.CopyEnvironmentPipelinesToProjectAsync(5, "staging", 1, "system", ct: TestContext.Current.CancellationToken);

        await _cliMock.Received(1).CommitFilesAsync(
            Arg.Any<string>(), "main", Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<string>(),
            PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_21_MoveToAnotherRepository_RecordsBothCommits()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _cliMock.CommitFileChangesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));

        var (outcome, _) = await _sut.ApplyProjectPipelineChangeAsync(
            new PipelineGitDefinitionLocation(1, "deploy", "main"),
            new PipelineGitDefinitionLocation(1, "deploy", "develop"),
            "name: deploy", "claude", ct: TestContext.Current.CancellationToken);

        Assert.Equal(GitWriteOutcome.Committed, outcome);
        // One commit on the new branch, one removal on the old: each one marks the repository written.
        await _commitRecorderMock.Received(2).RecordCommitAsync(1, Arg.Any<CancellationToken>());
        await _cliMock.Received(2).CommitFileChangesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), PipelineGitService.ServiceAuthorName, PipelineGitService.ServiceAuthorEmail,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CopyEnvironmentPipelinesToProjectAsync_CommitFails_ReturnsZero()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _pipelineRepoMock.GetPipelinesByEnvironmentAsync(5, Arg.Any<CancellationToken>())
            .Returns([new Pipeline { Id = 1, Name = "build", YamlDefinition = "steps: []" }]);
        _cliMock.CommitFilesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, (string?)null, "error"));

        var result = await _sut.CopyEnvironmentPipelinesToProjectAsync(5, "staging", 1, "admin", ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
    }

    // --- Helpers ---

    [Fact]
    public async Task R483_GetPipelineSourceAsync_ReadsTheRepositoryOnce_AndGivesItsHeadCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupRepoWithDiskPath(1, "my-repo", "develop");
        _cliMock.GetCommitsAsync(Arg.Any<string>(), "develop", skip: 0, take: 1, ct: Arg.Any<CancellationToken>())
            .Returns([new GitLightCommitDto { Sha = "c755999fc0" }]);

        var source = await _sut.GetPipelineSourceAsync(1, "Aetheus Candidate", ct);

        Assert.NotNull(source);
        Assert.Equal(("develop", "c755999fc0", ".pipeline/aetheus-candidate.yaml"), (source.Branch, source.CommitHash, source.Path));
        // The stored repository list is read once and reused for the head commit; the syncing list
        // (default branch detection: two git processes and a possible write) is never asked for.
        await _gitServiceMock.Received(1).GetAccessibleRepositoriesAsync(Arg.Any<List<int>?>(), Arg.Any<CancellationToken>());
        await _gitServiceMock.DidNotReceiveWithAnyArgs().GetRepositoriesAsync(default, ct);
        await _cliMock.DidNotReceiveWithAnyArgs().DetectDefaultBranchAsync(string.Empty, ct);
    }

    [Fact]
    public async Task R483_GetPipelineSourceAsync_AnUnreadableHead_IsAnUnresolvedSource_NotAFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        SetupRepoWithDiskPath(1, "my-repo", "develop");
        _cliMock.GetCommitsAsync(Arg.Any<string>(), "develop", skip: 0, take: 1, ct: Arg.Any<CancellationToken>())
            .Returns<List<GitLightCommitDto>>(_ => throw new InvalidOperationException("git failed"));

        var source = await _sut.GetPipelineSourceAsync(1, "deploy", ct);

        Assert.NotNull(source);
        Assert.Null(source.CommitHash);
    }

    [Theory]
    [InlineData(null, "main")]
    [InlineData("release/2.0", "release/2.0")]
    public async Task GetHeadCommitShaAsync_ReadsTheAskedBranchElseTheDefault(string? sourceBranch, string expectedBranch)
    {
        SetupRepoWithDiskPath(31, "head-repo", "main");
        _cliMock.GetCommitsAsync(Arg.Any<string>(), expectedBranch, 0, 1, ct: Arg.Any<CancellationToken>())
            .Returns([new GitLightCommitDto { Sha = "0123456789abcdef0123456789abcdef01234567" }]);

        var sha = await _sut.GetHeadCommitShaAsync(31, TestContext.Current.CancellationToken, sourceBranch);

        Assert.Equal("0123456789abcdef0123456789abcdef01234567", sha);
    }

    [Fact]
    public async Task GetHeadCommitShaAsync_AnEmptyRepositoryOrNoRepository_HasNoHead()
    {
        _gitServiceMock.GetRepositoriesAsync(32, Arg.Any<CancellationToken>())
            .Returns([new GitLightRepoDto { Id = 1, ProjectId = 32, Slug = "empty", IsEmpty = true }]);

        Assert.Null(await _sut.GetHeadCommitShaAsync(32, TestContext.Current.CancellationToken));
        Assert.Null(await _sut.GetHeadCommitShaAsync(33, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetHeadCommitShaAsync_AFailingRead_IsAnUnresolvedSource()
    {
        _gitServiceMock.GetRepositoriesAsync(34, Arg.Any<CancellationToken>())
            .Returns<List<GitLightRepoDto>>(_ => throw new IOException("repository store unavailable"));

        Assert.Null(await _sut.GetHeadCommitShaAsync(34, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetHeadCommitShaAsync_ACancelledCaller_IsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _gitServiceMock.GetRepositoriesAsync(35, Arg.Any<CancellationToken>())
            .Returns<List<GitLightRepoDto>>(_ => throw new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sut.GetHeadCommitShaAsync(35, cancelled.Token));
    }

    private void SetupRepoWithDiskPath(int projectId, string slug, string defaultBranch)
    {
        List<GitLightRepoDto> repos = [new GitLightRepoDto
            {
                Id = 1,
                ProjectId = projectId,
                Slug = slug,
                DefaultBranch = defaultBranch,
                IsEmpty = false
            }];
        _gitServiceMock.GetRepositoriesAsync(projectId, Arg.Any<CancellationToken>()).Returns(repos);
        _gitServiceMock.GetAccessibleRepositoriesAsync(
                Arg.Is<List<int>?>(ids => ids != null && ids.Contains(projectId)), Arg.Any<CancellationToken>())
            .Returns(repos);

        // ResolveDiskPath returns a path that exists in tests - use temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"pgs-test-{projectId}-{slug}");
        Directory.CreateDirectory(tempDir);
        _gitServiceMock.ResolveDiskPath(projectId, slug).Returns(tempDir);
    }

    [Fact]
    public async Task R1_21_UserSave_IsAuthoredByAetheusWithTheNoReplyAddress_AndNamesTheRequester()
    {
        SetupRepoWithDiskPath(1, "my-repo", "main");
        _senderResolverMock.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(new EmailSenderResolution { NoReplyAddress = "no-reply@example.test" });
        _cliMock.CommitFileAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, "abc123", (string?)null));

        await _sut.WriteProjectPipelineYamlAsync(1, "deploy", "steps: []", "claude", ct: TestContext.Current.CancellationToken);

        await _cliMock.Received(1).CommitFileAsync(
            Arg.Any<string>(), "main", Arg.Any<string>(), "steps: []",
            Arg.Is<string>(m => m.EndsWith("\n\nRequested-by: claude", StringComparison.Ordinal)),
            "Aetheus", "no-reply@example.test", Arg.Any<CancellationToken>());
    }
}
