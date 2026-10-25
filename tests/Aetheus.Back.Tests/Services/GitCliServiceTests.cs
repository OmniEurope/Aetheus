// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests;

public class GitCliServiceTests
{
    private readonly GitCliService _sut = new(NullLogger<GitCliService>.Instance, new Aetheus.Back.Components.Git.GitProcessRunner(NullLogger<Aetheus.Back.Components.Git.GitProcessRunner>.Instance));

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task ListReleaseBranches_BlankUrl_Throws(string url)
        => await Assert.ThrowsAsync<ArgumentException>(() => _sut.ListReleaseBranchesAsync(url, ct: TestContext.Current.CancellationToken));

    [Theory]
    // Disallowed schemes (SSRF / option-injection hardening) - rejected before any git invocation.
    [InlineData("file:///tmp/repo")]
    [InlineData("http://github.com/acme/demo.git")]
    [InlineData("ssh://git@github.com/acme/demo.git")]
    [InlineData("ftp://host/repo")]
    [InlineData("not-a-valid-url")]
    // Loopback / private / link-local hosts over https are still rejected.
    [InlineData("https://localhost/acme/demo.git")]
    [InlineData("https://127.0.0.1/acme/demo.git")]
    [InlineData("https://[::1]/acme/demo.git")]
    [InlineData("https://10.1.2.3/acme/demo.git")]
    [InlineData("https://172.16.5.5/acme/demo.git")]
    [InlineData("https://192.168.1.10/acme/demo.git")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://0.0.0.0/repo")]
    [InlineData("https://100.64.0.1/repo")]
    [InlineData("https://198.18.0.1/repo")]
    [InlineData("https://224.0.0.1/repo")]
    [InlineData("https://[fe80::1]/repo")]
    [InlineData("https://[fc00::1]/repo")]
    [InlineData("https://[::ffff:10.0.0.1]/repo")]
    [InlineData("https://user:token@github.com/acme/demo.git")]
    public async Task ListReleaseBranches_DisallowedUrl_ReturnsEmpty(string url)
    {
        var result = await _sut.ListReleaseBranchesAsync(url, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveSafeEndpoint_DnsToLoopback_IsRejectedBeforeGitStarts()
    {
        var result = await GitCliService.ResolveSafeEndpointAsync("https://localhost/repository.git", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567\trefs/heads/main\n", "main", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef01234567\trefs/heads/other\n", "main", null)]
    [InlineData("invalid\trefs/heads/main\n", "main", null)]
    public void ParseBranchCommit_AcceptsOnlyTheSelectedBranchAndFullHash(string output, string branch, string? expected)
        => Assert.Equal(expected, GitCliService.ParseBranchCommit(output, branch));

    [Fact]
    public void ParseDefaultHead_ResolvesTheRemoteBranchAndFullHash()
    {
        var result = GitCliService.ParseDefaultHead(
            "ref: refs/heads/develop\tHEAD\n0123456789abcdef0123456789abcdef01234567\tHEAD\n");

        Assert.Equal(new GitRemoteBranch("develop", "0123456789abcdef0123456789abcdef01234567"), result);
    }

    [Theory]
    [InlineData("file:///tmp/repo")]
    [InlineData("https://localhost/repo.git")]
    [InlineData("https://user:token@github.com/example/repo.git")]
    public async Task ResolveBranchCommit_DisallowedUrl_FailsBeforeGitStarts(string url)
        => await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ResolveBranchCommitAsync(url, "main", TestContext.Current.CancellationToken));

    [Fact]
    public void ListReleaseBranches_NeverFollowsARedirectAwayFromThePinnedAddress()
    {
        var psi = GitCliService.BuildListReleaseBranchesStartInfo(
            "https://github.com/acme/demo.git", ("github.com", 443, "140.82.121.4"));
        var args = psi.ArgumentList.ToList();
        var lsRemote = args.IndexOf("ls-remote");

        var redirect = args.IndexOf("http.followRedirects=false");
        Assert.True(redirect > 0 && redirect < lsRemote, string.Join(' ', args));
        Assert.Equal("-c", args[redirect - 1]);
        Assert.Contains("http.curloptResolve=github.com:443:140.82.121.4", args);
    }

    [Theory]
    [InlineData("release/2.1", "--heads", "refs/heads/release/2.1")]
    [InlineData(null, "--symref", "HEAD")]
    [InlineData(" ", "--symref", "HEAD")]
    public void ResolveBranchCommit_AsksOnlyTheSelectedRefThroughThePinnedAddress(string? branch, string mode, string reference)
    {
        var psi = GitCliService.BuildResolveBranchStartInfo(
            "https://github.com/acme/demo.git", branch, ("github.com", 443, "140.82.121.4"));

        Assert.Equal(
            [
                "-c", "protocol.file.allow=never",
                "-c", "http.curloptResolve=github.com:443:140.82.121.4",
                "-c", "http.followRedirects=false",
                "ls-remote", "--exit-code", mode, "--", "https://github.com/acme/demo.git", reference,
            ],
            psi.ArgumentList);
        Assert.Equal("0", psi.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", psi.Environment["GCM_INTERACTIVE"]);
    }

    [Fact]
    public void ParseLsRemote_ReadsTheDefaultHeadOrTheAskedBranch()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";

        Assert.Equal(new GitRemoteBranch("main", hash), GitCliService.ParseLsRemote($"ref: refs/heads/main\tHEAD\n{hash}\tHEAD\n", null));
        Assert.Equal(new GitRemoteBranch("dev", hash), GitCliService.ParseLsRemote($"{hash}\trefs/heads/dev\n", "dev"));
        Assert.Null(GitCliService.ParseLsRemote($"{hash}\trefs/heads/other\n", "dev"));
    }

    [Theory]
    [InlineData("http://github.com/acme/demo.git", "0123456789abcdef0123456789abcdef01234567", "A public HTTPS repository and a full commit hash are required.")]
    [InlineData("https://github.com/acme/demo.git", "0123456", "A public HTTPS repository and a full commit hash are required.")]
    [InlineData("https://github.com/acme/demo.git", "z123456789abcdef0123456789abcdef01234567", "A public HTTPS repository and a full commit hash are required.")]
    public async Task ReadPipelineYaml_WithoutAPublicRepositoryAndAFullHash_IsRefusedBeforeGitStarts(string url, string commit, string message)
    {
        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ReadPipelineYamlAsync(url, commit, "ci", TestContext.Current.CancellationToken));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task RunLsRemote_ReadsTheAskedBranchAndTheDefaultHeadOfARealRepository()
    {
        var repository = CreateRepositoryWithOneCommit(out var commit);
        try
        {
            var branch = await GitCliService.RunLsRemoteAsync(
                LsRemote(repository, "--heads", "refs/heads/main"), "main", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var head = await GitCliService.RunLsRemoteAsync(
                LsRemote(repository, "--symref", "HEAD"), null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(new GitRemoteBranch("main", commit), branch);
            Assert.Equal(new GitRemoteBranch("main", commit), head);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task RunLsRemote_AnUnknownBranch_IsRefused()
    {
        var repository = CreateRepositoryWithOneCommit(out _);
        try
        {
            var error = await Assert.ThrowsAsync<BadRequestException>(() => GitCliService.RunLsRemoteAsync(
                LsRemote(repository, "--heads", "refs/heads/missing"), "missing", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

            Assert.Equal("The external repository or selected branch could not be reached.", error.Message);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task RunLsRemote_NoAnswerBeforeTheDeadline_IsRefusedAsATimeout()
    {
        // A git that never answers: it waits on a standard input that stays open until it is killed.
        // A zero deadline over a real ls-remote was not enough: the deadline fires from a timer, and on a
        // loaded machine the ls-remote could finish first. Run outside any repository, so killing it
        // leaves no directory in use.
        var silent = Aetheus.Back.Components.Git.GitProcessStartInfoFactory.Create(Path.GetTempPath(), ["hash-object", "--stdin"]);
        silent.RedirectStandardInput = true;

        var error = await Assert.ThrowsAsync<BadRequestException>(() => GitCliService.RunLsRemoteAsync(
            silent, "main", TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));

        Assert.Equal("The external repository did not respond before the timeout.", error.Message);
    }

    [Fact]
    public async Task RunListReleaseBranches_ReadsOnlyTheReleaseBranchesOfARealRepository()
    {
        var repository = CreateRepositoryWithOneCommit(out _);
        try
        {
            Git(repository, "branch", "release/v1.2.0");
            Git(repository, "branch", "release/2.0");
            Git(repository, "branch", "release/V3");
            Git(repository, "branch", "release/v");
            Git(repository, "branch", "feature/release");

            var branches = await _sut.RunListReleaseBranchesAsync(
                LsRemote(repository, "--heads", "refs/heads/release/*"), repository, TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [("release/2.0", "2.0"), ("release/V3", "3"), ("release/v1.2.0", "1.2.0")],
                branches.OrderBy(branch => branch.BranchName, StringComparer.Ordinal));
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task RunListReleaseBranches_NoReleaseBranch_ReturnsEmpty()
    {
        var repository = CreateRepositoryWithOneCommit(out _);
        try
        {
            var branches = await _sut.RunListReleaseBranchesAsync(
                Aetheus.Back.Components.Git.GitProcessStartInfoFactory.Create(
                    repository, ["ls-remote", "--heads", "--", repository, "refs/heads/release/*"]),
                repository, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Empty(branches);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task RunListReleaseBranches_AFailedRun_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"aetheus-missing-{Guid.NewGuid():N}");

        var branches = await _sut.RunListReleaseBranchesAsync(
            Aetheus.Back.Components.Git.GitProcessStartInfoFactory.Create(
                Path.GetTempPath(), ["ls-remote", "--heads", "--", missing, "refs/heads/release/*"]),
            "https://user:token@example.invalid/repo.git", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task RunListReleaseBranches_NoAnswerBeforeTheDeadline_ReturnsEmpty()
    {
        // Same silent git as the ls-remote timeout: it waits on a standard input left open until killed.
        var silent = Aetheus.Back.Components.Git.GitProcessStartInfoFactory.Create(Path.GetTempPath(), ["hash-object", "--stdin"]);
        silent.RedirectStandardInput = true;

        var branches = await _sut.RunListReleaseBranchesAsync(
            silent, "https://example.invalid/repo.git", TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task RunListReleaseBranches_CancelledByTheCaller_Propagates()
    {
        var silent = Aetheus.Back.Components.Git.GitProcessStartInfoFactory.Create(Path.GetTempPath(), ["hash-object", "--stdin"]);
        silent.RedirectStandardInput = true;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sut.RunListReleaseBranchesAsync(
            silent, "https://example.invalid/repo.git", TimeSpan.FromSeconds(30), cancelled.Token));
    }

    private static System.Diagnostics.ProcessStartInfo LsRemote(string repository, string mode, string reference) =>
        Aetheus.Back.Components.Git.GitProcessStartInfoFactory.Create(
            repository, ["ls-remote", "--exit-code", mode, "--", repository, reference]);

    private static string CreateRepositoryWithOneCommit(out string commit)
    {
        var path = Directory.CreateTempSubdirectory("aetheus-ls-remote-").FullName;
        Git(path, "init", "--initial-branch=main");
        Git(path, "-c", "user.email=tests@aetheus.invalid", "-c", "user.name=Aetheus Tests",
            "commit", "--allow-empty", "-m", "first");
        commit = Git(path, "rev-parse", "HEAD");
        return path;
    }

    private static string Git(string repository, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(
            Aetheus.Back.Components.Git.GitProcessStartInfoFactory.Create(repository, arguments))!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private static void DeleteRepository(string repository)
    {
        foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(repository, recursive: true);
    }
}
