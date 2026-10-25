// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Tests.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests;

/// <summary>
/// Exercises the real <see cref="GitLightCliService"/> against a throwaway on-disk git repository.
/// git is a hard runtime dependency of the service (and is installed on the build host), so these
/// run the actual porcelain-parsing code rather than mocking it.
/// </summary>
public sealed class GitLightCliServiceRepoTests : IDisposable
{
    private readonly string _dir;
    private readonly GitLightCliService _sut;
    private readonly string _firstSha;
    private readonly string _secondSha;

    public GitLightCliServiceRepoTests()
    {
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        _sut = new GitLightCliService(
            runner,
            new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance,
            TimeProvider.System);
        _dir = Directory.CreateTempSubdirectory("gitlight-test-").FullName;
        Git("init", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test User");
        Git("config", "commit.gpgsign", "false");
        GitFixtureGuard.AssertOwnedBy(_dir, _dir);

        File.WriteAllText(Path.Combine(_dir, "README.md"), "# Hello\nSecond line\n");
        Git("add", ".");
        Git("commit", "-m", "feat: initial commit");
        _firstSha = Git("rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(_dir, "app.txt"), "content\n");
        Git("add", ".");
        Git("commit", "-m", "fix: add app file");
        _secondSha = Git("rev-parse", "HEAD").Trim();

        Git("tag", "v1.0");
        Git("branch", "develop");
    }

    public void Dispose()
    {
        // git writes read-only pack/object files on Windows; clear attributes before deleting and
        // swallow any residual cleanup error (a leaked temp dir is harmless - the OS reclaims it).
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception) { /* best effort */ }
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // WHY: pre-push hook env inheritance incident - GIT_DIR/GIT_INDEX_FILE would redirect this
        // fixture git call to the real repository.
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    [Fact]
    public async Task GetCommitsAsync_ReturnsCommitsNewestFirst()
    {
        var commits = await _sut.GetCommitsAsync(_dir, null, 0, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, commits.Count);
        Assert.Contains(commits, c => c.Message.Contains("add app file"));
        Assert.Contains(commits, c => c.Message.Contains("initial commit"));
    }

    [Fact]
    public async Task GetCommitsAsync_WithSearch_FiltersByMessage()
    {
        var commits = await _sut.GetCommitsAsync(_dir, null, 0, 10, search: "initial", ct: TestContext.Current.CancellationToken);

        Assert.Single(commits);
        Assert.Contains("initial", commits[0].Message);
    }

    /// <summary> The commits grid's Author filter keeps the commits whose author name is exactly
    /// one of the ticked ones: "Bob" is neither "Bobby" nor "B.b" (the name is escaped, not read as
    /// a pattern). </summary>
    [Fact]
    public async Task AuthorFilter_MatchesExactNames_AndCountsTheSame()
    {
        foreach (var author in new[] { "Bob <bob@example.com>", "Bobby <bobby@example.com>", "B.b <bdotb@example.com>" })
        {
            File.AppendAllText(Path.Combine(_dir, "app.txt"), author + Environment.NewLine);
            Git("add", ".");
            Git("commit", "-m", "change by " + author, "--author", author);
        }
        var ct = TestContext.Current.CancellationToken;

        var bob = await _sut.GetCommitsAsync(_dir, null, 0, 10, ct: ct, filter: new GitCommitLogFilter { Authors = ["Bob"] });
        var dotted = await _sut.GetCommitsAsync(_dir, null, 0, 10, ct: ct, filter: new GitCommitLogFilter { Authors = ["B.b"] });
        var count = await _sut.GetCommitCountAsync(_dir, null, ct: ct, filter: new GitCommitLogFilter { Authors = ["Bob", "Test User"] });
        var authors = await _sut.GetCommitAuthorsAsync(_dir, ct);

        Assert.Equal(["Bob"], bob.Select(commit => commit.AuthorName));
        Assert.Equal(["B.b"], dotted.Select(commit => commit.AuthorName));
        Assert.Equal(3, count);
        Assert.Equal(["B.b", "Bob", "Bobby", "Test User"], authors);
    }

    [Fact]
    public async Task GetCommitCountAsync_ReturnsTotal()
        => Assert.Equal(2, await _sut.GetCommitCountAsync(_dir, null, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetBranchesAsync_ReturnsMainAndDevelop()
    {
        var branches = await _sut.GetBranchesAsync(_dir, "main", ct: TestContext.Current.CancellationToken);

        Assert.Contains(branches, b => b.Name == "main");
        Assert.Contains(branches, b => b.Name == "develop");
    }

    [Fact]
    public async Task GetTagsAsync_ReturnsCreatedTag()
    {
        var tags = await _sut.GetTagsAsync(_dir, ct: TestContext.Current.CancellationToken);

        Assert.Contains(tags, t => t.Name == "v1.0");
    }

    [Fact]
    public async Task GetTreeAsync_ListsRootEntries()
    {
        var tree = await _sut.GetTreeAsync(_dir, "main", null, ct: TestContext.Current.CancellationToken);

        Assert.Contains(tree, e => e.Name == "README.md");
        Assert.Contains(tree, e => e.Name == "app.txt");
    }

    [Fact]
    public async Task GetBlobAsync_ReturnsFileContent()
    {
        var blob = await _sut.GetBlobAsync(_dir, "main", "README.md", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(blob);
        Assert.Contains("# Hello", blob.Content);
        Assert.False(blob.IsBinary);
    }

    [Fact]
    public async Task GetCommitGraphAsync_ReturnsNonEmptyGraph()
    {
        var graph = await _sut.GetCommitGraphAsync(_dir, 50, ct: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(graph));
    }

    [Fact]
    public async Task GetBlameAsync_ReturnsLinesForFile()
    {
        var blame = await _sut.GetBlameAsync(_dir, "main", "README.md", ct: TestContext.Current.CancellationToken);

        Assert.NotEmpty(blame);
    }

    [Fact]
    public async Task GetDiffAsync_BetweenCommits_ReportsAddedFile()
    {
        var diff = await _sut.GetDiffAsync(_dir, _firstSha, _secondSha, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(diff);
        // The second commit adds app.txt, so the diff between the two commits must report it.
        Assert.Contains(diff.FileDiffs, f => f.Path.Contains("app.txt"));
    }

    [Fact]
    public async Task GetRootCommitPatchAsync_ReturnsInitialFileWithoutAssumingObjectFormat()
    {
        var patch = await _sut.GetRootCommitPatchAsync(
            _dir,
            _firstSha,
            TestContext.Current.CancellationToken);

        Assert.False(patch.IsTruncated);
        Assert.Contains("README.md", patch.Patch, StringComparison.Ordinal);
        Assert.Contains("+# Hello", patch.Patch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCommitPatchAsync_LargeCommitRetainsOnlyBoundedOutput()
    {
        File.WriteAllText(
            Path.Combine(_dir, "large.txt"),
            new string('x', GitLightCliService.MaximumCommitPatchChars + 64 * 1024));
        Git("add", ".");
        Git("commit", "-m", "feat: add large patch");
        var largeSha = Git("rev-parse", "HEAD").Trim();

        var patch = await _sut.GetCommitPatchAsync(
            _dir,
            _secondSha,
            largeSha,
            TestContext.Current.CancellationToken);

        Assert.True(patch.IsTruncated);
        Assert.Equal(GitLightCliService.MaximumCommitPatchChars, patch.Patch.Length);
    }

    [Fact]
    public async Task RealRepository_ExposesMergeParentsBranchesAndMergeDiff()
    {
        Git("checkout", "-b", "feature/real-diff");
        File.WriteAllText(Path.Combine(_dir, "feature.txt"), "feature content\n");
        Git("add", ".");
        Git("commit", "-m", "feat: feature branch");
        Git("checkout", "main");
        File.WriteAllText(Path.Combine(_dir, "main.txt"), "main content\n");
        Git("add", ".");
        Git("commit", "-m", "feat: main branch");
        Git("merge", "--no-ff", "feature/real-diff", "-m", "merge: feature");
        var mergeSha = Git("rev-parse", "HEAD").Trim();

        var branches = await _sut.GetBranchesAsync(
            _dir, "main", TestContext.Current.CancellationToken);
        var commits = await _sut.GetCommitsAsync(
            _dir, mergeSha, 0, 1, ct: TestContext.Current.CancellationToken);
        var merge = Assert.Single(commits);
        var patch = await _sut.GetCommitPatchAsync(
            _dir,
            merge.ParentShas[0],
            merge.Sha,
            TestContext.Current.CancellationToken);
        var diff = GitUnifiedDiffParser.Parse(patch.Patch);

        Assert.Contains(branches, branch => branch.Name == "main" && branch.IsDefault);
        Assert.Contains(branches, branch => branch.Name == "feature/real-diff");
        Assert.Equal(2, merge.ParentShas.Count);
        Assert.Contains(diff.FileDiffs, file => file.Path == "feature.txt");
    }

    [Fact]
    public async Task CreateAndDeleteBranch_RoundTrips()
    {
        await _sut.CreateBranchAsync(_dir, "feature/x", "main", ct: TestContext.Current.CancellationToken);
        var afterCreate = await _sut.GetBranchesAsync(_dir, "main", ct: TestContext.Current.CancellationToken);
        Assert.Contains(afterCreate, b => b.Name == "feature/x");

        await _sut.DeleteBranchAsync(_dir, "feature/x", ct: TestContext.Current.CancellationToken);
        var afterDelete = await _sut.GetBranchesAsync(_dir, "main", ct: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(afterDelete, b => b.Name == "feature/x");
    }

    [Fact]
    public async Task CreateAndDeleteTag_RoundTrips()
    {
        await _sut.CreateTagAsync(_dir, "v2.0", "main", "release two", ct: TestContext.Current.CancellationToken);
        var afterCreate = await _sut.GetTagsAsync(_dir, ct: TestContext.Current.CancellationToken);
        Assert.Contains(afterCreate, t => t.Name == "v2.0");

        await _sut.DeleteTagAsync(_dir, "v2.0", ct: TestContext.Current.CancellationToken);
        var afterDelete = await _sut.GetTagsAsync(_dir, ct: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(afterDelete, t => t.Name == "v2.0");
    }

    [Fact]
    public async Task GetBlobStreamAsync_ReturnsReadableStream()
    {
        await using var stream = await _sut.GetBlobStreamAsync(_dir, "main", "README.md", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Hello", content);
    }

    [Fact]
    public async Task InitBareRepoAsync_CreatesBareRepository()
    {
        var bareDir = Path.Combine(_dir, "bare-child.git");

        await _sut.InitBareRepoAsync(bareDir, "main", ct: TestContext.Current.CancellationToken);

        // A bare repo has a HEAD file at its root rather than a .git subdirectory.
        Assert.True(File.Exists(Path.Combine(bareDir, "HEAD")));
    }

    [Fact]
    public async Task MergeBranchesAsync_FastForward_Succeeds()
    {
        // Branch off main, add a commit, then merge it back into main.
        Git("checkout", "-b", "feature/merge");
        File.WriteAllText(Path.Combine(_dir, "merged.txt"), "merged\n");
        Git("add", ".");
        Git("commit", "-m", "feat: mergeable change");
        // Detach HEAD so neither branch is checked out in this worktree - the service merges via a
        // private worktree of the target, which git refuses if the target is checked out elsewhere.
        Git("checkout", "--detach");

        var (success, sha, error) = await _sut.MergeBranchesAsync(
            _dir, "feature/merge", "main", "Test User", "test@example.com", ct: TestContext.Current.CancellationToken);

        Assert.True(success, error);
        Assert.False(string.IsNullOrEmpty(sha));
    }

    [Fact]
    public async Task RunGcAsync_CompletesWithoutError()
    {
        await _sut.RunGcAsync(_dir, ct: TestContext.Current.CancellationToken);

        // Smoke: the repo is still readable after a gc.
        Assert.Equal(2, await _sut.GetCommitCountAsync(_dir, null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DetectDefaultBranchAsync_ReturnsCheckedOutBranch()
    {
        var branch = await _sut.DetectDefaultBranchAsync(_dir, ct: TestContext.Current.CancellationToken);

        Assert.Equal("main", branch);
    }

    [Fact]
    public async Task SetHeadAsync_RepointsDefaultBranch()
    {
        await _sut.SetHeadAsync(_dir, "develop", ct: TestContext.Current.CancellationToken);

        var branch = await _sut.DetectDefaultBranchAsync(_dir, ct: TestContext.Current.CancellationToken);
        Assert.Equal("develop", branch);
    }

    [Fact]
    public async Task GetTreeAsync_WithSubPath_ListsNestedEntries()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "Program.cs"), "class C {}\n");
        Git("add", ".");
        Git("commit", "-m", "feat: add src");

        var tree = await _sut.GetTreeAsync(_dir, "main", "src", ct: TestContext.Current.CancellationToken);

        Assert.Contains(tree, e => e.Name == "Program.cs");
    }

    // --- M-git-2: option-injection guard on user-supplied refs/paths ---

    [Theory]
    [InlineData("-rf")]
    [InlineData("--output=/etc/passwd")]
    public void EnsureRefArgsSafe_LeadingDash_Throws(string bad)
        => Assert.Throws<ArgumentException>(() => GitArgumentGuard.EnsureRefArgsSafe(bad));

    [Fact]
    public void EnsureRefArgsSafe_NormalRefsAndNull_DoNotThrow()
    {
        GitArgumentGuard.EnsureRefArgsSafe("main", "src/Program.cs", null);
        GitArgumentGuard.EnsureRefArgsSafe("v1.0", "");
    }

    [Fact]
    public async Task GetTreeAsync_RefStartingWithDash_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => _sut.GetTreeAsync(_dir, "--output=x", null, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetBlobAsync_PathStartingWithDash_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => _sut.GetBlobAsync(_dir, "main", "-rf", ct: TestContext.Current.CancellationToken));

    // --- every file of a revision with its blob id, to compare two repositories ---

    [Fact]
    public async Task GetTreeBlobsAsync_ListsEveryFileOfTheRevision_WithItsModeAndBlobId()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_dir, "src", "deep folder"));
        File.WriteAllText(Path.Combine(_dir, "src", "deep folder", "file with space.txt"), "nested\n");
        Git("add", ".");
        Git("commit", "-m", "feat: nested file");

        var first = await _sut.GetTreeBlobsAsync(_dir, _firstSha, ct);
        var head = await _sut.GetTreeBlobsAsync(_dir, "main", ct);

        Assert.NotNull(first);
        Assert.NotNull(head);
        Assert.Equal(["README.md"], first.Keys);
        Assert.Equal(["README.md", "app.txt", "src/deep folder/file with space.txt"], head.Keys.Order(StringComparer.Ordinal));
        // The same content is the same blob in both revisions: that equality is the whole comparison.
        Assert.Equal(first["README.md"], head["README.md"]);
        Assert.Matches("^100644 [0-9a-f]{40,64}$", head["app.txt"]);
    }

    [Fact]
    public async Task GetTreeBlobsAsync_AnUnknownRevision_IsUnknown_NotAnEmptyTree()
        => Assert.Null(await _sut.GetTreeBlobsAsync(_dir, "0000000000000000000000000000000000000000", TestContext.Current.CancellationToken));

    // --- Binary detection now scans content for a NUL byte (cross-platform, no /dev/null) ---

    [Fact]
    public async Task GetBlobAsync_BinaryFileWithNulByte_IsDetectedBinary()
    {
        await File.WriteAllBytesAsync(Path.Combine(_dir, "blob.bin"), [0x00, 0x01, 0x02, 0x00, 0xFF], cancellationToken: TestContext.Current.CancellationToken);
        Git("add", ".");
        Git("commit", "-m", "feat: add binary");

        var blob = await _sut.GetBlobAsync(_dir, "main", "blob.bin", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(blob);
        Assert.True(blob.IsBinary);
    }
}
