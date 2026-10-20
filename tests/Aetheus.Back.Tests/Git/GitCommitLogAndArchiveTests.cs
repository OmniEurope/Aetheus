// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.IO.Compression;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Tests.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests;

/// <summary>Exercise archive streaming and commit filters against a real temporary Git repository,
/// with totals matching paged results.</summary>
public sealed class GitCommitLogAndArchiveTests : IDisposable
{
    private readonly string _dir;
    private readonly GitLightCliService _sut;

    public GitCommitLogAndArchiveTests()
    {
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        _sut = new GitLightCliService(
            runner,
            new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance),
            NullLogger<GitLightCliService>.Instance,
            TimeProvider.System);
        _dir = Directory.CreateTempSubdirectory("gitlog-test-").FullName;
        Git("init", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test User");
        Git("config", "commit.gpgsign", "false");
        GitFixtureGuard.AssertOwnedBy(_dir, _dir);

        Commit("README.md", "# Hello\n", "feat: initial commit", "2026-01-10T10:00:00Z");
        Git("checkout", "-b", "feature/login");
        Commit("login.txt", "login\n", "feat: add login", "2026-02-10T10:00:00Z");
        Git("checkout", "main");
        Commit("app.txt", "app\n", "fix: add app file", "2026-03-10T10:00:00Z");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception) { /* best effort: a leaked temp dir is harmless */ }
    }

    [Fact]
    public async Task Archive_StreamsAZipOfTheRef_UnderThePrefixFolder()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var stream = await _sut.GetArchiveStreamAsync(_dir, "feature/login", "my-repo", ct);

        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("my-repo/README.md", names);
        Assert.Contains("my-repo/login.txt", names);
        Assert.DoesNotContain("my-repo/app.txt", names);
    }

    [Fact]
    public async Task Archive_UnknownRef_IsNull_BeforeAnyByteStreams()
        => Assert.Null(await _sut.GetArchiveStreamAsync(_dir, "no-such-branch", "my-repo", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("--output=/tmp/x")]
    [InlineData("-o")]
    public async Task Archive_RefReadAsAnOption_IsRefused(string reference)
        => await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.GetArchiveStreamAsync(_dir, reference, "my-repo", TestContext.Current.CancellationToken));

    [Fact]
    public async Task EveryRefWalk_NamesTheBranchEachCommitWasReachedThrough()
    {
        var commits = await _sut.GetCommitsAsync(_dir, null, 0, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, commits.Count);
        Assert.Equal("feature/login", commits.Single(commit => commit.Message == "feat: add login").SourceRef);
        Assert.Equal("main", commits.Single(commit => commit.Message == "fix: add app file").SourceRef);
        Assert.All(commits, commit => Assert.False(string.IsNullOrEmpty(commit.SourceRef)));
    }

    [Fact]
    public async Task BranchFilter_WalksOnlyTheTickedBranches_AndCountsTheSame()
    {
        var ct = TestContext.Current.CancellationToken;
        var filter = new GitCommitLogFilter { Branches = ["feature/login"] };

        var commits = await _sut.GetCommitsAsync(_dir, null, 0, 10, ct: ct, filter: filter);
        var count = await _sut.GetCommitCountAsync(_dir, null, ct: ct, filter: filter);

        Assert.Equal(["feat: add login", "feat: initial commit"], commits.Select(commit => commit.Message));
        Assert.All(commits, commit => Assert.Equal("feature/login", commit.SourceRef));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task MessageFilter_IsALiteralCaseInsensitiveContains()
    {
        var ct = TestContext.Current.CancellationToken;

        var add = await _sut.GetCommitsAsync(_dir, null, 0, 10, ct: ct, filter: new GitCommitLogFilter { Message = "ADD" });
        var dotted = await _sut.GetCommitsAsync(_dir, null, 0, 10, ct: ct, filter: new GitCommitLogFilter { Message = "a.d" });

        Assert.Equal(2, add.Count);
        Assert.Empty(dotted); // "." is matched literally, not as "any character" ("add")
    }

    [Fact]
    public async Task DateFilter_KeepsTheCommitterDatesInsideTheRange_AndCountsTheSame()
    {
        var ct = TestContext.Current.CancellationToken;
        var filter = new GitCommitLogFilter
        {
            Since = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            Until = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc)
        };

        var commits = await _sut.GetCommitsAsync(_dir, null, 0, 10, ct: ct, filter: filter);
        var count = await _sut.GetCommitCountAsync(_dir, null, ct: ct, filter: filter);

        var commit = Assert.Single(commits);
        Assert.Equal("feat: add login", commit.Message);
        Assert.Equal(new DateTime(2026, 2, 10, 10, 0, 0, DateTimeKind.Utc), commit.CommitDate.ToUniversalTime());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CommitWalkFromASha_HasNoBranch()
    {
        var sha = Git("rev-parse", "main").Trim();

        var commit = Assert.Single(await _sut.GetCommitsAsync(_dir, sha, 0, 1, ct: TestContext.Current.CancellationToken));

        Assert.Null(commit.SourceRef);
    }

    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("main", "main")]
    [InlineData("refs/tags/v1.0", "tags/v1.0")]
    [InlineData("0123456789abcdef0123456789abcdef01234567", null)]
    [InlineData("", null)]
    public void SourceRef_ReadsAsTheBranchShortName(string source, string? expected)
        => Assert.Equal(expected, GitCommitLogReader.ParseSourceRef(source));

    private void Commit(string file, string content, string message, string date)
    {
        File.WriteAllText(Path.Combine(_dir, file), content);
        Git("add", ".");
        // The committer date is what --since/--until filter on, so the fixture pins it per commit.
        Run(date, "commit", "-m", message, "--date", date);
    }

    private string Git(params string[] args) => Run(null, args);

    private string Run(string? committerDate, params string[] args)
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
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        if (committerDate is not null) psi.Environment["GIT_COMMITTER_DATE"] = committerDate;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
