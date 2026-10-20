// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Tests.Git;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary> The <c>source:</c> block of a definition. The workspace comes from another repository
/// of the project, pinned to a commit, and a run is refused rather than started on a source that
/// cannot be trusted. The comparison runs against real git repositories: a blob id is what makes
/// "identical" a fact, and only git computes it. </summary>
public sealed class PipelineWorkspaceSourceResolverTests : IDisposable
{
    private const string DefinitionCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly IGitLightService _git = Substitute.For<IGitLightService>();
    private readonly IGitLightCliService _cli = Substitute.For<IGitLightCliService>();
    private readonly IPipelineGitService _pipelineGit = Substitute.For<IPipelineGitService>();
    private readonly IExternalMirrorRefresher _mirrors = Substitute.For<IExternalMirrorRefresher>();
    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-source-").FullName;

    public PipelineWorkspaceSourceResolverTests()
    {
        // A substitute answers a string with "", which here would read as a fetch error.
        _mirrors.RefreshAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>()).Returns(
        [
            new GitLightRepoDto { Id = 1, ProjectId = 7, Slug = "aetheus", DefaultBranch = "develop", CloneUrl = "https://api/git/7/aetheus.git" },
            new GitLightRepoDto
            {
                Id = 2, ProjectId = 7, Slug = "aetheus-public", DefaultBranch = "main",
                CloneUrl = "https://api/git/7/aetheus-public.git", IsAdditionalSource = true
            }
        ]);
        _git.ResolveDiskPath(7, "aetheus").Returns("/repos/7/aetheus.git");
        _git.ResolveDiskPath(7, "aetheus-public").Returns("/repos/7/aetheus-public.git");
        _cli.GetCommitsAsync("/repos/7/aetheus-public.git", "main", skip: 0, take: 1, ct: Arg.Any<CancellationToken>())
            .Returns([new GitLightCommitDto { Sha = SourceCommit }]);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception) { /* best effort: a leaked temp directory is harmless */ }
    }

    private PipelineWorkspaceSourceResolver Build(IGitLightCliService? cli = null) =>
        new(_git, cli ?? _cli, _pipelineGit, _mirrors);

    private static PipelineSourceDefinition Source(bool mustMatch = false, string? excludeFile = null, string? branch = null) =>
        new() { Repository = "aetheus-public", Branch = branch, MustMatchDefinition = mustMatch, MatchExcludeFile = excludeFile };

    [Fact]
    public async Task TheSource_IsFetchedThenPinnedToTheHeadOfItsDefaultBranch()
    {
        var ct = TestContext.Current.CancellationToken;

        var workspace = await Build().ResolveAsync(7, Source(), definitionRepositoryId: 1, DefinitionCommit, ct);

        Assert.Equal((2, "https://api/git/7/aetheus-public.git", "main", SourceCommit),
            (workspace.RepositoryId, workspace.CloneUrl, workspace.Branch, workspace.CommitHash));
        // Fetched before it is read: the run builds what the remote holds now.
        Received.InOrder(() =>
        {
            _mirrors.RefreshAsync(7, "aetheus-public", Arg.Any<CancellationToken>());
            _git.GetRepositoriesAsync(7, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ADeclaredBranch_IsTheOnePinned()
    {
        _cli.GetCommitsAsync("/repos/7/aetheus-public.git", "release/1", skip: 0, take: 1, ct: Arg.Any<CancellationToken>())
            .Returns([new GitLightCommitDto { Sha = DefinitionCommit }]);

        var workspace = await Build().ResolveAsync(
            7, Source(branch: "release/1"), 1, DefinitionCommit, TestContext.Current.CancellationToken);

        Assert.Equal(("release/1", DefinitionCommit), (workspace.Branch, workspace.CommitHash));
    }

    [Fact]
    public async Task APinnedCommit_IsCheckedOut_InsteadOfTheBranchHead()
    {
        const string earlier = "cccccccccccccccccccccccccccccccccccccccc";
        _cli.GetCommitsAsync("/repos/7/aetheus-public.git", earlier, skip: 0, take: 1, ct: Arg.Any<CancellationToken>())
            .Returns([new GitLightCommitDto { Sha = earlier }]);

        var workspace = await Build().ResolveAsync(7, Source(), 1, DefinitionCommit, TestContext.Current.CancellationToken, earlier);

        Assert.Equal(("main", earlier), (workspace.Branch, workspace.CommitHash));
    }

    [Fact]
    public async Task APinnedCommitTheSourceNoLongerHolds_IsRefused()
    {
        const string gone = "dddddddddddddddddddddddddddddddddddddddd";
        _cli.GetCommitsAsync("/repos/7/aetheus-public.git", gone, skip: 0, take: 1, ct: Arg.Any<CancellationToken>())
            .Returns([]);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() =>
            Build().ResolveAsync(7, Source(), 1, DefinitionCommit, TestContext.Current.CancellationToken, gone));

        Assert.Contains(gone, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryTheProjectDoesNotHave_IsRefused()
    {
        var error = await Assert.ThrowsAsync<BadRequestException>(() => Build().ResolveAsync(
            7, new PipelineSourceDefinition { Repository = "elsewhere" }, 1, DefinitionCommit, TestContext.Current.CancellationToken));

        Assert.Contains("'elsewhere' is not attached", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemoteThatCannotBeFetched_RefusesTheRun_RatherThanBuildingAStaleMirror()
    {
        _mirrors.RefreshAsync(7, "aetheus-public", Arg.Any<CancellationToken>()).Returns("could not resolve host github.com");

        var error = await Assert.ThrowsAsync<BadRequestException>(() => Build().ResolveAsync(
            7, Source(), 1, DefinitionCommit, TestContext.Current.CancellationToken));

        Assert.Contains("could not resolve host github.com", error.Message, StringComparison.Ordinal);
        await _cli.DidNotReceiveWithAnyArgs().GetCommitsAsync(string.Empty, null, 0, 0, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ABranchWithoutACommit_IsRefused()
    {
        _cli.GetCommitsAsync("/repos/7/aetheus-public.git", "main", skip: 0, take: 1, ct: Arg.Any<CancellationToken>())
            .Returns([]);

        var error = await Assert.ThrowsAsync<BadRequestException>(() => Build().ResolveAsync(
            7, Source(), 1, DefinitionCommit, TestContext.Current.CancellationToken));

        Assert.Contains("could not be pinned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MustMatch_WithAMissingExcludeFile_IsRefused()
    {
        _pipelineGit.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/public/exclude.txt", DefinitionCommit, Arg.Any<CancellationToken>(), 1)
            .Returns((string?)null);

        var error = await Assert.ThrowsAsync<BadRequestException>(() => Build().ResolveAsync(
            7, Source(mustMatch: true, excludeFile: ".pipeline/configs/public/exclude.txt"), 1, DefinitionCommit,
            TestContext.Current.CancellationToken));

        Assert.Contains("was not found at the definition's revision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_NamesWhatIsMissingChangedAndUnexpected_OnceTheExcludedPathsAreSetAside()
    {
        var definition = new Dictionary<string, string>
        {
            ["src/app.cs"] = "100644 a1",
            ["src/run.sh"] = "100755 b1",
            ["README.md"] = "100644 c1",
            [".pipeline/nightly.yaml"] = "100644 d1",
            ["docs/plans/PLAN-001.md"] = "100644 e1"
        };
        var source = new Dictionary<string, string>
        {
            ["src/app.cs"] = "100644 a2",
            ["src/run.sh"] = "100644 b1",
            ["extra.txt"] = "100644 f1"
        };

        var differences = PipelineWorkspaceSourceResolver.Compare(definition, source, [".pipeline/**", "docs/plans/**"]);

        // Same content with another mode (the executable bit) is a change too.
        Assert.Equal(
            ["missing: README.md", "changed: src/app.cs", "changed: src/run.sh", "unexpected: extra.txt"],
            differences);
    }

    [Fact]
    public void ParseExcludedPaths_SkipsBlankLinesAndComments()
    {
        var patterns = PipelineWorkspaceSourceResolver.ParseExcludedPaths(
            "# private\r\n.pipeline/**\r\n\r\n  docs/plans/**  \n#end\n");

        Assert.Equal([".pipeline/**", "docs/plans/**"], patterns);
    }

    [Fact]
    public async Task MustMatch_AgainstRealRepositories_AcceptsTheExactCopy_AndRefusesAnyOtherTree()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        var realCli = new GitLightCliService(
            runner, new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance, TimeProvider.System);

        // The private repository: public files, plus a pipeline definition that is not published.
        var privateRepo = NewRepository("private", "develop");
        Write(privateRepo, "src/app.cs", "class App;\n");
        Write(privateRepo, "README.md", "# Product\n");
        Write(privateRepo, ".pipeline/nightly.yaml", "name: nightly\n");
        Write(privateRepo, ".pipeline/configs/public/exclude.txt", "# not published\n.pipeline/**\n");
        var privateCommit = Commit(privateRepo, "private revision");

        // The public copy of that revision, without the excluded paths.
        var publicRepo = NewRepository("public", "main");
        Write(publicRepo, "src/app.cs", "class App;\n");
        Write(publicRepo, "README.md", "# Product\n");
        var publicCommit = Commit(publicRepo, "public copy");

        _git.ResolveDiskPath(7, "aetheus").Returns(privateRepo);
        _git.ResolveDiskPath(7, "aetheus-public").Returns(publicRepo);
        _pipelineGit.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/public/exclude.txt", privateCommit, Arg.Any<CancellationToken>(), 1)
            .Returns("# not published\n.pipeline/**\n");
        var source = Source(mustMatch: true, excludeFile: ".pipeline/configs/public/exclude.txt");

        var workspace = await Build(realCli).ResolveAsync(7, source, 1, privateCommit, ct);
        Assert.Equal(publicCommit, workspace.CommitHash);

        // One byte of difference in the public repository: the run is refused, and the path is named.
        Write(publicRepo, "src/app.cs", "class App; // tampered\n");
        var tampered = Commit(publicRepo, "tampered");

        var error = await Assert.ThrowsAsync<BadRequestException>(() => Build(realCli).ResolveAsync(7, source, 1, privateCommit, ct));

        Assert.Contains("1 path(s) differ (changed: src/app.cs)", error.Message, StringComparison.Ordinal);
        Assert.Contains(tampered[..8], error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchBranch_ComparesWithTheRecordedExport_NotWithTheDefinitionRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        var realCli = new GitLightCliService(
            runner, new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance, TimeProvider.System);

        // The private repository: its working branch, and the record of what was exported (an adapted
        // tree: a renamed launcher, which no exclusion list could describe). Neutral names on purpose: the
        // public export rewrites the real launcher names in every file, this one included.
        var privateRepo = NewRepository("private", "develop");
        Write(privateRepo, "private-launcher.ps1", "launch\n");
        Write(privateRepo, "src/app.cs", "class App;\n");
        var developCommit = Commit(privateRepo, "private revision");
        Git(privateRepo, "checkout", "--orphan", "public-distribution");
        Git(privateRepo, "rm", "-r", "--cached", ".");
        File.Delete(Path.Combine(privateRepo, "private-launcher.ps1"));
        Write(privateRepo, "public-launcher.ps1", "launch\n");
        var exportCommit = Commit(privateRepo, "export");
        Git(privateRepo, "checkout", "develop");

        var publicRepo = NewRepository("public", "main");
        Write(publicRepo, "public-launcher.ps1", "launch\n");
        Write(publicRepo, "src/app.cs", "class App;\n");
        Commit(publicRepo, "export");

        _git.ResolveDiskPath(7, "aetheus").Returns(privateRepo);
        _git.ResolveDiskPath(7, "aetheus-public").Returns(publicRepo);
        var source = new PipelineSourceDefinition
        {
            Repository = "aetheus-public",
            MustMatchDefinition = true,
            MatchBranch = "public-distribution"
        };

        // Identical to the recorded export: accepted, although it differs from the develop tree.
        await Build(realCli).ResolveAsync(7, source, 1, developCommit, ct);

        // Against the definition's own revision the same source is refused, and the paths are named.
        var strict = await Assert.ThrowsAsync<BadRequestException>(() => Build(realCli).ResolveAsync(
            7, source with { MatchBranch = null }, 1, developCommit, ct));
        Assert.Contains("missing: private-launcher.ps1", strict.Message, StringComparison.Ordinal);
        Assert.Contains("unexpected: public-launcher.ps1", strict.Message, StringComparison.Ordinal);

        // A public repository that moved away from the export is refused against the branch too.
        Write(publicRepo, "src/app.cs", "class App; // tampered\n");
        Commit(publicRepo, "tampered");
        var error = await Assert.ThrowsAsync<BadRequestException>(() => Build(realCli).ResolveAsync(7, source, 1, developCommit, ct));
        Assert.Contains("branch 'public-distribution' of 'aetheus'", error.Message, StringComparison.Ordinal);
        Assert.Contains(exportCommit[..8], error.Message, StringComparison.Ordinal);

        var unknown = await Assert.ThrowsAsync<BadRequestException>(() => Build(realCli).ResolveAsync(
            7, source with { MatchBranch = "no-such-branch" }, 1, developCommit, ct));
        Assert.Contains("could not be pinned", unknown.Message, StringComparison.Ordinal);
    }

    private string NewRepository(string name, string branch)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        Git(directory, "init", "-b", branch);
        Git(directory, "config", "user.email", "test@aetheus.invalid");
        Git(directory, "config", "user.name", "Aetheus Test");
        Git(directory, "config", "commit.gpgsign", "false");
        GitFixtureGuard.AssertOwnedBy(directory, _root);
        return directory;
    }

    private static void Write(string repository, string relativePath, string content)
    {
        var path = Path.Combine(repository, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Commit(string repository, string message)
    {
        Git(repository, "add", "--all");
        Git(repository, "commit", "-m", message);
        return Git(repository, "rev-parse", "HEAD").Trim();
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // A hook environment would redirect this fixture call to the real repository.
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) start.Environment.Remove(name);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
