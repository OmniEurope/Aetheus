// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
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
}
