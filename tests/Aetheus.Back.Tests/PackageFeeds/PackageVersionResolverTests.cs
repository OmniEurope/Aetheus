// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.PackageFeeds;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageFeeds;

public class PackageVersionResolverTests
{
    private static PackageVersionResolver Build(HttpStatusCode status, string body)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("package-feeds").Returns(new HttpClient(new StubHandler(status, body)));
        return new PackageVersionResolver(
            factory,
            NullLogger<PackageVersionResolver>.Instance,
            TimeProvider.System);
    }

    [Theory]
    [InlineData(PackageFeedType.Docker)]
    [InlineData(PackageFeedType.Generic)]
    public async Task Resolve_UnsupportedType_ReturnsUnsupported(PackageFeedType type)
    {
        var sut = Build(HttpStatusCode.OK, "{}");
        var result = await sut.ResolveLatestAsync(type, "https://example.com", "pkg", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.Unsupported, result.Outcome);
    }

    [Fact]
    public async Task Resolve_NuGet_ParsesLatestVersion()
    {
        var sut = Build(HttpStatusCode.OK, "{\"versions\":[\"12.0.0\",\"13.0.3\"]}");
        var result = await sut.ResolveLatestAsync(PackageFeedType.NuGet, "https://api.nuget.org", "Newtonsoft.Json", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("13.0.3", result.LatestVersion);
    }

    [Fact]
    public async Task Resolve_Npm_ParsesLatestAndPublishedDate()
    {
        var sut = Build(HttpStatusCode.OK, "{\"dist-tags\":{\"latest\":\"5.1.0\"},\"time\":{\"5.1.0\":\"2026-01-02T03:04:05Z\"}}");
        var result = await sut.ResolveLatestAsync(PackageFeedType.Npm, "https://registry.npmjs.org", "left-pad", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("5.1.0", result.LatestVersion);
        Assert.NotNull(result.PublishedAt);
    }

    [Fact]
    public async Task Resolve_PyPi_ParsesInfoVersion()
    {
        var sut = Build(HttpStatusCode.OK, "{\"info\":{\"version\":\"2.31.0\"},\"releases\":{}}");
        var result = await sut.ResolveLatestAsync(PackageFeedType.PyPI, "https://pypi.org", "requests", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("2.31.0", result.LatestVersion);
    }

    [Fact]
    public async Task Resolve_NotFound_ReturnsNotFound()
    {
        var sut = Build(HttpStatusCode.NotFound, "");
        var result = await sut.ResolveLatestAsync(PackageFeedType.NuGet, "https://api.nuget.org", "does.not.exist", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Resolve_NuGet_PrefersStableOverTrailingPrerelease()
    {
        // Flat container lists ascending; a trailing prerelease must not be surfaced as "latest stable".
        var sut = Build(HttpStatusCode.OK, "{\"versions\":[\"12.0.0\",\"13.0.3\",\"14.0.0-beta1\"]}");
        var result = await sut.ResolveLatestAsync(PackageFeedType.NuGet, "https://api.nuget.org", "Newtonsoft.Json", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("13.0.3", result.LatestVersion);
    }

    [Fact]
    public async Task Resolve_NuGet_AllPrerelease_FallsBackToLast()
    {
        var sut = Build(HttpStatusCode.OK, "{\"versions\":[\"1.0.0-alpha\",\"1.0.0-beta\"]}");
        var result = await sut.ResolveLatestAsync(PackageFeedType.NuGet, "https://api.nuget.org", "only.pre", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("1.0.0-beta", result.LatestVersion);
    }

    [Fact]
    public async Task Resolve_MalformedJson_ReturnsError_NeverFabricates()
    {
        var sut = Build(HttpStatusCode.OK, "{ this is not json");
        var result = await sut.ResolveLatestAsync(PackageFeedType.NuGet, "https://api.nuget.org", "corrupt", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.Error, result.Outcome);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task Resolve_ServerError_ReturnsError()
    {
        var sut = Build(HttpStatusCode.InternalServerError, "");
        var result = await sut.ResolveLatestAsync(PackageFeedType.Npm, "https://registry.npmjs.org", "throttled", ct: TestContext.Current.CancellationToken);
        Assert.Equal(PackageResolveOutcome.Error, result.Outcome);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task Resolve_RateLimited_PreservesRetryAfter()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("package-feeds").Returns(new HttpClient(new RateLimitedHandler()));
        var sut = new PackageVersionResolver(
            factory,
            NullLogger<PackageVersionResolver>.Instance,
            TimeProvider.System);

        var result = await sut.ResolveLatestAsync(
            PackageFeedType.Npm,
            "https://registry.npmjs.org",
            "throttled",
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class RateLimitedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        }
    }
}
