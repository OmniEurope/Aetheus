// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Back.Tests.Git;

/// <summary>
/// Exercises the post-deployment branch advance against a REAL temporary repository laid out the way
/// the service resolves one ({root}/{projectId}/{slug}.git). Mocking git here would prove nothing:
/// the whole safety property is that git itself refuses a non-fast-forward ref update.
/// </summary>
public sealed class GitBranchAdvanceServiceTests : IDisposable
{
    private const int ProjectId = 7;
    private const string Slug = "aetheus";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-advance-{Guid.NewGuid():N}");
    private readonly string _bare;
    private readonly string _work;
    private readonly IGitLightRepository _repos = Substitute.For<IGitLightRepository>();
    private readonly GitBranchAdvanceService _sut;

    public GitBranchAdvanceServiceTests()
    {
        _bare = Path.Combine(_root, ProjectId.ToString(), $"{Slug}.git");
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.GetDirectoryName(_bare)!);
        RunGit(_root, $"init --bare --initial-branch main \"{_bare}\"");
        RunGit(_root, $"clone \"{_bare}\" \"{_work}\"");
        RunGit(_work, "config user.email tester@aetheus.test");
        RunGit(_work, "config user.name Tester");
        GitFixtureGuard.AssertOwnedBy(_work, _root);

        _repos.GetByProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitInternalRepo { Id = 1, ProjectId = ProjectId, Slug = Slug, Name = "Aetheus" }]);

        _sut = new GitBranchAdvanceService(
            _repos,
            new GitProcessRunner(NullLogger<GitProcessRunner>.Instance),
            Options.Create(new GitLightOptions { RepositoriesPath = _root }),
            NullLogger<GitBranchAdvanceService>.Instance);
    }

    [Fact]
    public async Task Advances_The_Branch_When_The_Target_Is_A_Descendant()
    {
        var first = Commit("one");
        var second = Commit("two");
        SetBareRef("refs/heads/main", first);

        var result = await _sut.TryFastForwardAsync(
            ProjectId, "main", second, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.Advanced, result.Outcome);
        Assert.Equal(first, result.PreviousSha);
        Assert.Equal(Slug, result.RepositorySlug);
        Assert.Equal(second, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task R2105_A_Branch_Already_Past_The_Commit_Contains_It_And_Is_Left_Alone()
    {
        // Recette R2-105: 'main' pushed past the deployed candidate (and the rollback case, an older
        // release deployed): the branch already holds the commit, it is not moved backwards.
        var first = Commit("one");
        var second = Commit("two");
        SetBareRef("refs/heads/main", second);

        var result = await _sut.TryFastForwardAsync(
            ProjectId, "main", first, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.AlreadyContains, result.Outcome);
        Assert.Equal(second, result.PreviousSha);
        Assert.Equal(second, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task Refuses_To_Move_A_Diverged_Branch()
    {
        var root = Commit("one");
        var onMain = Commit("two");
        RunGit(_work, $"reset --quiet --hard {root}");
        var released = Commit("three");
        SetBareRef("refs/heads/main", onMain);

        var result = await _sut.TryFastForwardAsync(
            ProjectId, "main", released, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.NotFastForward, result.Outcome);
        Assert.Equal(onMain, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task Reports_AlreadyUpToDate_Without_Touching_The_Ref()
    {
        var only = Commit("one");
        SetBareRef("refs/heads/main", only);

        var result = await _sut.TryFastForwardAsync(
            ProjectId, "main", only, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.AlreadyUpToDate, result.Outcome);
        Assert.Equal(only, ReadBareRef("refs/heads/main"));
    }

    [Fact]
    public async Task Creates_The_Branch_When_It_Does_Not_Exist_Yet()
    {
        var only = Commit("one");

        var result = await _sut.TryFastForwardAsync(
            ProjectId, "production", only, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.Advanced, result.Outcome);
        Assert.Null(result.PreviousSha);
        Assert.Equal(only, ReadBareRef("refs/heads/production"));
    }

    [Fact]
    public async Task Reports_CommitNotFound_When_No_Repository_Holds_The_Commit()
    {
        Commit("one");
        var unknown = new string('a', 40);

        var result = await _sut.TryFastForwardAsync(
            ProjectId, "main", unknown, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.CommitNotFound, result.Outcome);
    }

    [Theory]
    // A leading '-' would reach git's argv as an option rather than a ref.
    [InlineData("--upload-pack=touch /tmp/pwned", "0000000000000000000000000000000000000000")]
    [InlineData("main", "not-a-sha")]
    [InlineData("main", "abc123")] // abbreviated: a full object id is required
    [InlineData("bad..name", "0000000000000000000000000000000000000000")]
    public async Task Refuses_Malformed_Input_Before_Reaching_Git(string branch, string sha)
    {
        var result = await _sut.TryFastForwardAsync(
            ProjectId, branch, sha, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.Refused, result.Outcome);
        await _repos.DidNotReceive().GetByProjectAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_Not_Escape_The_Repositories_Root_For_A_Traversing_Slug()
    {
        _repos.GetByProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns([new GitInternalRepo { Id = 2, ProjectId = ProjectId, Slug = "../../evil", Name = "Evil" }]);
        var only = Commit("one");

        var result = await _sut.TryFastForwardAsync(
            ProjectId, "main", only, TestContext.Current.CancellationToken);

        Assert.Equal(GitBranchAdvanceOutcome.CommitNotFound, result.Outcome);
    }

    // Each commit is pushed to a holding ref outside refs/heads so the bare repo actually owns the
    // object (the service only considers a repository that contains the commit) without any branch
    // under test existing yet. The branch tips themselves are then set explicitly by SetBareRef, so
    // every test states the exact starting position it is about.
    private string Commit(string content)
    {
        File.WriteAllText(Path.Combine(_work, "file.txt"), content);
        RunGit(_work, "add file.txt");
        RunGit(_work, $"commit -m \"{content}\"");
        var sha = RunGit(_work, "rev-parse HEAD").Trim();
        RunGit(_work, $"push --quiet origin +{sha}:refs/fixtures/objects");
        return sha;
    }

    private void SetBareRef(string reference, string sha) =>
        RunGit(_work, $"push --quiet origin +{sha}:{reference}");

    [Fact]
    public async Task ReleaseTag_IsCreatedOnTheDeployedCommit_AndAReplayFindsItThere()
    {
        Commit("one");
        var deployed = Commit("two");
        var ct = TestContext.Current.CancellationToken;

        var created = await _sut.TryCreateTagAsync(ProjectId, "v1.3.15", deployed, ct);
        var replay = await _sut.TryCreateTagAsync(ProjectId, "v1.3.15", deployed, ct);

        Assert.Equal(GitTagOutcome.Created, created.Outcome);
        Assert.Equal(Slug, created.RepositorySlug);
        Assert.Equal(deployed, ReadBareRef("refs/tags/v1.3.15"));
        Assert.Equal(GitTagOutcome.AlreadyThere, replay.Outcome);
    }

    [Fact]
    public async Task ReleaseTag_OnAnotherCommit_IsNeverMoved()
    {
        var first = Commit("one");
        var second = Commit("two");
        SetBareRef("refs/tags/v1.3.15", first);

        var result = await _sut.TryCreateTagAsync(ProjectId, "v1.3.15", second, TestContext.Current.CancellationToken);

        Assert.Equal(GitTagOutcome.OnAnotherCommit, result.Outcome);
        Assert.Equal(first, result.ExistingSha);
        Assert.Equal(first, ReadBareRef("refs/tags/v1.3.15"));
    }

    [Theory]
    [InlineData("v1..3")]
    [InlineData("v1.3 15")]
    [InlineData("v1.3.15.lock")]
    public async Task ReleaseTag_WithANameGitRefuses_IsRefused_AndNothingIsWritten(string tag)
    {
        var commit = Commit("one");

        var result = await _sut.TryCreateTagAsync(ProjectId, tag, commit, TestContext.Current.CancellationToken);

        Assert.Equal(GitTagOutcome.Refused, result.Outcome);
        Assert.Equal(string.Empty, RunGit(_bare, "tag --list").Trim());
    }

    [Fact]
    public async Task ReleaseTag_ForACommitNoRepositoryHolds_ReportsCommitNotFound()
    {
        Commit("one");

        var result = await _sut.TryCreateTagAsync(
            ProjectId, "v1.3.15", "abcdefabcdefabcdefabcdefabcdefabcdefabcd", TestContext.Current.CancellationToken);

        Assert.Equal(GitTagOutcome.CommitNotFound, result.Outcome);
    }

    private string ReadBareRef(string reference) =>
        RunGit(_bare, $"rev-parse --verify {reference}").Trim();

    private static string RunGit(string workDir, string args)
    {
        var git = ExecutableLocator.Require("git");
        var psi = new ProcessStartInfo(git, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // WHY: pre-push hook env inheritance incident - GIT_DIR/GIT_INDEX_FILE would redirect this
        // fixture git call to the real repository.
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Could not start '{git}'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(20000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed ({process.ExitCode}): {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            GitProcessRunner.ClearReadOnlyAttributes(_root);
            Directory.Delete(_root, true);
        }
        catch (IOException) { /* best effort: a temp directory left behind fails nothing */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }
}
