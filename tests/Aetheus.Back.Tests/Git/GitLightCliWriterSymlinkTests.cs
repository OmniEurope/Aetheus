// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Back.Components.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Git;

/// <summary>
/// BACK-010: a symbolic link committed in a repository must never let a commit write leave the
/// temporary clone. The links are committed as git tree entries (mode 120000) with plumbing, so the
/// fixture holds a real link entry on every platform, whatever the file system can create.
/// </summary>
public sealed class GitLightCliWriterSymlinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-writer-link-{Guid.NewGuid():N}");
    private readonly string _bare;
    private readonly string _work;
    private readonly string _outside;
    private readonly GitLightCliWriter _writer;

    public GitLightCliWriterSymlinkTests()
    {
        _bare = Path.Combine(_root, "repo.git");
        _work = Path.Combine(_root, "seed");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_outside);
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        _writer = new GitLightCliWriter(runner, NullLogger<GitLightCliWriter>.Instance);
        Git(_root, "init", "--bare", "--initial-branch", "main", _bare);
        Git(_root, "init", "--initial-branch", "main", _work);
        GitFixtureGuard.AssertOwnedBy(_bare, _root);
        GitFixtureGuard.AssertOwnedBy(_work, _root);
    }

    [Fact]
    public async Task CommitFileAsync_ThroughACommittedDirectoryLink_RefusesAndWritesNothingOutside()
    {
        SeedLink(".pipeline", _outside);

        var (ok, _, error) = await _writer.CommitFileAsync(
            _bare, "main", ".pipeline/ci.yaml", "name: ci\n", "add ci", "aetheus", "aetheus@test",
            TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
        Assert.Equal("120000", TreeMode(".pipeline")); // the repository still holds the link, untouched
    }

    [Fact]
    public async Task CommitFileAsync_OverACommittedFileLink_RefusesAndLeavesTheLinkAndItsTarget()
    {
        // git keeps the link mode of a committed link even when core.symlinks=false checks it out as
        // a plain file: writing over it would commit a link to the new text, so it is refused.
        var named = Path.Combine(_outside, "file.txt");
        SeedLink("link.txt", named);

        var (ok, _, error) = await _writer.CommitFileAsync(
            _bare, "main", "link.txt", "x", "replace link", "aetheus", "aetheus@test",
            TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Contains("link.txt is a symbolic link in the repository", error, StringComparison.Ordinal);
        Assert.False(File.Exists(named));
        Assert.Equal("120000", TreeMode("link.txt"));
    }

    [Fact]
    public async Task CommitFileAsync_NextToACommittedLink_StillCommitsOrdinaryFiles()
    {
        SeedLink(".pipeline", _outside);

        var (ok, _, error) = await _writer.CommitFileAsync(
            _bare, "main", "ci/ci.yaml", "name: ci\n", "add ci", "aetheus", "aetheus@test",
            TestContext.Current.CancellationToken);

        Assert.True(ok, error);
        Assert.Equal("name: ci", Git(_root, "--git-dir", _bare, "show", "main:ci/ci.yaml"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    /// <summary>
    /// The clone the writer works in checks a committed link out as a regular file holding the link
    /// text (core.symlinks=false), on every platform: on Linux a plain clone would create a real link.
    /// </summary>
    [Fact]
    public async Task CloneTargetBranchAsync_ChecksACommittedLinkOutAsARegularFile()
    {
        SeedLink(".pipeline", _outside);
        var clone = Path.Combine(_root, "writer-clone");

        var error = await _writer.CloneTargetBranchAsync(_bare, "main", clone, TestContext.Current.CancellationToken);

        Assert.Null(error);
        var checkedOut = new FileInfo(Path.Combine(clone, ".pipeline"));
        Assert.True(checkedOut.Exists, "the link is checked out as a regular file");
        Assert.Null(checkedOut.LinkTarget);
        Assert.Equal(0, (int)(checkedOut.Attributes & FileAttributes.ReparsePoint));
        Assert.Equal(_outside.Replace('\\', '/'), File.ReadAllText(checkedOut.FullName));
        Assert.Equal("false", Git(clone, "config", "--get", "core.symlinks"));
    }

    [Fact]
    public void HasLinkOnTheWay_FindsARealDirectoryLinkAndIgnoresPlainOrMissingComponents()
    {
        var clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(Path.Combine(clone, "plain"));
        CreateDirectoryLink(Path.Combine(clone, "linked"), _outside);
        try
        {
            Assert.True(GitLightCliWriter.HasLinkOnTheWay(clone, Path.Combine(clone, "linked", "ci.yaml")));
            Assert.True(GitLightCliWriter.HasLinkOnTheWay(clone, Path.Combine(clone, "linked")));
            Assert.False(GitLightCliWriter.HasLinkOnTheWay(clone, Path.Combine(clone, "plain", "ci.yaml")));
            Assert.False(GitLightCliWriter.HasLinkOnTheWay(clone, Path.Combine(clone, "missing", "deeper", "ci.yaml")));
        }
        finally
        {
            if (new DirectoryInfo(Path.Combine(clone, "linked")).LinkTarget is not null)
                Directory.Delete(Path.Combine(clone, "linked"));
        }
    }

    // Commits <path> as a symbolic link to <linkTarget> on main and pushes it to the bare repository.
    private void SeedLink(string path, string linkTarget)
    {
        var text = Path.Combine(_root, "link-text");
        File.WriteAllText(text, linkTarget.Replace('\\', '/'));
        var blob = Git(_work, "hash-object", "-w", text);
        Git(_work, "update-index", "--add", "--cacheinfo", $"120000,{blob},{path}");
        Git(_work, "-c", "user.name=seed", "-c", "user.email=seed@test", "commit", "-m", "seed link");
        Git(_work, "push", _bare, "HEAD:main");
        Assert.Equal("120000", TreeMode(path));
    }

    private string TreeMode(string path) =>
        Git(_root, "--git-dir", _bare, "ls-tree", "main", "--", path).Split(' ')[0];

    // A symbolic link where the OS allows it; on Windows without the SeCreateSymbolicLink privilege a
    // junction, the same reparse point for LinkTarget and needing no privilege.
    private static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var arg in new[] { "/c", "mklink", "/J", Path.GetFullPath(link), Path.GetFullPath(target) })
                psi.ArgumentList.Add(arg);
            using var mklink = Process.Start(psi) ?? throw new InvalidOperationException("Could not start cmd.exe.", ex);
            var stderr = mklink.StandardError.ReadToEnd();
            mklink.WaitForExit(10000);
            if (mklink.ExitCode != 0 || !Directory.Exists(link))
                throw new InvalidOperationException($"Could not create a junction at '{link}': {stderr}", ex);
        }
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    // git is a hard prerequisite of the product: a host without it fails these tests.
    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo(ExecutableLocator.Require("git"))
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        // WHY: pre-push hook env inheritance incident - GIT_DIR/GIT_INDEX_FILE would redirect this
        // fixture git call to the real repository.
        GitProcessStartInfoFactory.NeutralizeInheritedGitEnvironment(psi);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        return stdout.Result.Trim();
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            GitProcessRunner.ClearReadOnlyAttributes(_root);
            Directory.Delete(_root, true);
        }
        catch (IOException) { /* best effort: a temp directory left behind is harmless */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
