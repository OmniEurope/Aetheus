// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Tests.Git;

/// <summary>An external mirror can no longer be aimed at an internal service (SSRF beside audit BACK-024).</summary>
public class ExternalGitEndpointGuardTests
{
    private static ExternalGitEndpointGuard Guard(string[] answer, params string[] allowedInternalHosts) =>
        new(new GitLightOptions { AllowedInternalHosts = allowedInternalHosts },
            (_, _) => Task.FromResult(answer.Select(IPAddress.Parse).ToArray()));

    [Fact]
    public async Task PublicHttpsHost_IsPinned_AndRedirectsAreRefused()
    {
        var (args, refusal) = await Guard(["140.82.121.4"]).CheckAsync("https://github.com/o/r.git", TestContext.Current.CancellationToken);

        Assert.Null(refusal);
        Assert.Equal(["-c", "http.followRedirects=false", "-c", "http.curloptResolve=github.com:443:140.82.121.4"], args);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.1.10")]
    public async Task HostResolvingToAPrivateAddress_IsRefused(string address)
    {
        var (args, refusal) = await Guard([address]).CheckAsync("https://evil.example/o/r.git", TestContext.Current.CancellationToken);

        Assert.NotNull(refusal);
        Assert.Contains("GitLight:AllowedInternalHosts", refusal);
        Assert.Empty(args);
    }

    [Fact]
    public async Task MixedPublicAndPrivateAnswer_IsRefused()
    {
        var (_, refusal) = await Guard(["140.82.121.4", "10.0.0.5"]).CheckAsync("https://mixed.example/o/r.git", TestContext.Current.CancellationToken);

        Assert.NotNull(refusal);
    }

    [Fact]
    public async Task AllowedInternalHost_IsAccepted_StillWithoutRedirects()
    {
        var (args, refusal) = await Guard(["10.0.0.5"], "Gitea.lan").CheckAsync("https://gitea.lan/o/r.git", TestContext.Current.CancellationToken);

        Assert.Null(refusal);
        Assert.Equal(["-c", "http.followRedirects=false"], args);
    }

    [Fact]
    public async Task ScpLikeSshHost_IsChecked_ButNotPinned()
    {
        var cancellation = TestContext.Current.CancellationToken;

        var (privateArgs, privateRefusal) = await Guard(["10.0.0.5"]).CheckAsync("git@forge.internal:o/r.git", cancellation);
        var (publicArgs, publicRefusal) = await Guard(["140.82.121.4"]).CheckAsync("git@github.com:o/r.git", cancellation);

        Assert.NotNull(privateRefusal);
        Assert.Empty(privateArgs);
        Assert.Null(publicRefusal);
        Assert.Empty(publicArgs);
    }

    [Fact]
    public async Task LiteralPrivateIp_IsRefusedWithoutDns()
    {
        var guard = new ExternalGitEndpointGuard(new GitLightOptions(),
            (_, _) => throw new InvalidOperationException("DNS must not be queried for a literal address"));

        var (_, refusal) = await guard.CheckAsync("https://127.0.0.1/o/r.git", TestContext.Current.CancellationToken);

        Assert.NotNull(refusal);
    }
}
