// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.PackageFeeds;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageFeeds;

/// <summary>
/// The list of versions a registry publishes, read from answers recorded from the real registries on
/// 2026-11-01 (<c>Recorded/</c>, byte for byte as served: monolog/monolog on Packagist p2, Newtonsoft.Json on
/// the NuGet flat container, left-pad on npm, six on PyPI, commons-io on Maven Central). No test reaches the
/// network. <c>packagist-p2-monolog-truncated.json</c> is the first 3000 bytes of the real monolog answer (a reply cut
/// short), <c>packagist-p2-missing-404.txt</c> the real body of the 404. The case named <c>Synthetic</c> uses a
/// hand-written body because the real PyPI answer held no yanked release.
/// </summary>
public sealed class PackageVersionListTests
{
    private readonly List<string> _requested = [];

    private static string Recorded(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PackageFeeds", "Recorded", file));

    private PackageVersionResolver Build(HttpStatusCode status, string body)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("package-feeds").Returns(_ => new HttpClient(new Handler(status, body, _requested)));
        return new PackageVersionResolver(factory, NullLogger<PackageVersionResolver>.Instance, TimeProvider.System);
    }

    private static IReadOnlyList<string> Versions(PackageVersionListResult result) =>
        [.. result.Versions.Select(v => v.Version)];

    [Fact]
    public async Task Packagist_ListsEveryTaggedVersionOfTheRecordedAnswer_PreReleasesIncluded()
    {
        var sut = Build(HttpStatusCode.OK, Recorded("packagist-p2-monolog-monolog.json"));

        var result = await sut.ListVersionsAsync(
            PackageFeedType.Packagist, "", "monolog/monolog", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(91, result.Versions.Count);
        var versions = Versions(result);
        Assert.Contains("3.12.1", versions);
        Assert.Contains("3.0.0-RC1", versions);
        Assert.Contains("2.0.0-beta1", versions);
        Assert.Contains("1.0.0", versions);
        Assert.Equal("https://repo.packagist.org/p2/monolog/monolog.json", Assert.Single(_requested));
        Assert.All(result.Versions, v => Assert.NotNull(v.PublishedAt));
    }

    [Fact]
    public async Task Packagist_LatestIsNotResolvedHere_NeverGuessedFromTheOrderOfTheListing()
    {
        // The order of Composer versions is owned by a layer above this one, and the registry's own order is not a
        // guarantee: the latest version of a Packagist package is Unsupported, the list is what this offers.
        var sut = Build(HttpStatusCode.OK, Recorded("packagist-p2-monolog-monolog.json"));

        var latest = await sut.ResolveLatestAsync(
            PackageFeedType.Packagist, "", "monolog/monolog", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Unsupported, latest.Outcome);
        Assert.Null(latest.LatestVersion);
        Assert.Empty(_requested);
    }

    [Fact]
    public async Task Packagist_RecordedNotFoundAnswer_GivesNoVersion()
    {
        var sut = Build(HttpStatusCode.NotFound, Recorded("packagist-p2-missing-404.txt"));

        var result = await sut.ListVersionsAsync(
            PackageFeedType.Packagist, "", "aetheus-nonexistent/does-not-exist-pkg", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.NotFound, result.Outcome);
        Assert.Empty(result.Versions);
    }

    [Fact]
    public async Task Packagist_RecordedAnswerCutShort_GivesNoVersionNeverAGuess()
    {
        var sut = Build(HttpStatusCode.OK, Recorded("packagist-p2-monolog-truncated.json"));

        var listed = await sut.ListVersionsAsync(
            PackageFeedType.Packagist, "", "monolog/monolog", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Error, listed.Outcome);
        Assert.Empty(listed.Versions);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"packages":{}}""")]
    [InlineData("""{"packages":{"monolog/monolog":[]}}""")]
    [InlineData("""{"packages":{"monolog/monolog":"nope"}}""")]
    [InlineData("""{"packages":{"monolog/monolog":[{"name":"monolog/monolog"}]}}""")]
    public async Task Packagist_AnswerWithoutTheExpectedShape_GivesNoVersion(string body)
    {
        var sut = Build(HttpStatusCode.OK, body);

        var result = await sut.ListVersionsAsync(
            PackageFeedType.Packagist, "", "monolog/monolog", TestContext.Current.CancellationToken);

        Assert.NotEqual(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Empty(result.Versions);
    }

    [Theory]
    [InlineData("monolog")]
    [InlineData("../etc/passwd")]
    [InlineData("a/b/c")]
    [InlineData("vendor/pack age")]
    [InlineData("vendor/pack?x=1")]
    [InlineData("")]
    public async Task Packagist_NameThatIsNotVendorSlashPackage_IsRefusedBeforeAnyRequest(string name)
    {
        var sut = Build(HttpStatusCode.OK, "{}");

        var result = await sut.ListVersionsAsync(PackageFeedType.Packagist, "", name, TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.NotFound, result.Outcome);
        Assert.Empty(_requested);
    }

    [Fact]
    public async Task Packagist_NameIsLookedUpInLowerCase()
    {
        var sut = Build(HttpStatusCode.OK, Recorded("packagist-p2-monolog-monolog.json"));

        await sut.ListVersionsAsync(PackageFeedType.Packagist, "", "Monolog/Monolog", TestContext.Current.CancellationToken);

        Assert.Equal("https://repo.packagist.org/p2/monolog/monolog.json", Assert.Single(_requested));
    }

    [Fact]
    public async Task NuGet_ListsTheRecordedFlatContainer()
    {
        var sut = Build(HttpStatusCode.OK, Recorded("nuget-flatcontainer-newtonsoft-json.json"));

        var result = await sut.ListVersionsAsync(
            PackageFeedType.NuGet, "", "Newtonsoft.Json", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(86, result.Versions.Count);
        Assert.Contains("13.0.3", Versions(result));
        Assert.Contains("14.0.1-beta2", Versions(result));
        Assert.Equal("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json", Assert.Single(_requested));
    }

    [Fact]
    public async Task Npm_ListsTheVersionsOfTheRecordedPackument()
    {
        var sut = Build(HttpStatusCode.OK, Recorded("npm-packument-left-pad.json"));

        var result = await sut.ListVersionsAsync(
            PackageFeedType.Npm, "", "left-pad", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(15, result.Versions.Count);
        Assert.Contains("1.3.0", Versions(result));
        Assert.Contains("0.0.3", Versions(result));
        Assert.All(result.Versions, v => Assert.NotNull(v.PublishedAt));
    }

    [Fact]
    public async Task PyPi_ListsTheReleasesOfTheRecordedAnswer_PreReleaseIncluded()
    {
        var sut = Build(HttpStatusCode.OK, Recorded("pypi-json-six.json"));

        var result = await sut.ListVersionsAsync(
            PackageFeedType.PyPI, "", "six", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(29, result.Versions.Count);
        Assert.Contains("1.17.0", Versions(result));
        Assert.Contains("1.0b1", Versions(result));
    }

    [Fact]
    public async Task PyPi_SyntheticYanked_ReleaseWithOnlyYankedOrNoFilesIsNotListed()
    {
        const string body = """
            {"info":{"version":"2.0"},"releases":{
              "1.0":[{"yanked":false,"upload_time_iso_8601":"2024-01-01T00:00:00Z"}],
              "1.5":[{"yanked":true,"upload_time_iso_8601":"2024-02-01T00:00:00Z"}],
              "1.6":[],
              "2.0":[{"yanked":true},{"yanked":false,"upload_time_iso_8601":"2024-03-01T00:00:00Z"}]}}
            """;
        var sut = Build(HttpStatusCode.OK, body);

        var result = await sut.ListVersionsAsync(PackageFeedType.PyPI, "", "demo", TestContext.Current.CancellationToken);

        Assert.Equal(["1.0", "2.0"], Versions(result));
    }

    [Fact]
    public async Task Maven_ListsTheVersionsOfTheRecordedMetadata()
    {
        var sut = Build(HttpStatusCode.OK, Recorded("maven-metadata-commons-io.xml"));

        var result = await sut.ListVersionsAsync(
            PackageFeedType.Maven, "", "commons-io:commons-io", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(34, result.Versions.Count);
        Assert.Contains("2.22.0", Versions(result));
        Assert.Equal(
            "https://repo.maven.apache.org/maven2/commons-io/commons-io/maven-metadata.xml", Assert.Single(_requested));
    }

    [Theory]
    [InlineData(PackageFeedType.Docker)]
    [InlineData(PackageFeedType.Generic)]
    public async Task UnsupportedFeedType_GivesNoVersion(PackageFeedType type)
    {
        var sut = Build(HttpStatusCode.OK, "{}");

        var result = await sut.ListVersionsAsync(type, "https://example.com", "pkg", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Unsupported, result.Outcome);
        Assert.Empty(result.Versions);
        Assert.Empty(_requested);
    }

    [Theory]
    [InlineData(PackageFeedType.NuGet, "{ not json")]
    [InlineData(PackageFeedType.Npm, "{ not json")]
    [InlineData(PackageFeedType.PyPI, "{ not json")]
    [InlineData(PackageFeedType.Maven, "<metadata><versioning>")]
    [InlineData(PackageFeedType.NuGet, """{"versions":"nope"}""")]
    [InlineData(PackageFeedType.Npm, """{"versions":[]}""")]
    [InlineData(PackageFeedType.PyPI, """{"releases":[]}""")]
    public async Task UnreadableAnswer_GivesNoVersion(PackageFeedType type, string body)
    {
        var sut = Build(HttpStatusCode.OK, body);
        var name = type == PackageFeedType.Maven ? "org.example:demo" : "demo";

        var result = await sut.ListVersionsAsync(type, "", name, TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Error, result.Outcome);
        Assert.Empty(result.Versions);
    }

    [Fact]
    public async Task ServerError_GivesNoVersion()
    {
        var sut = Build(HttpStatusCode.InternalServerError, "");

        var result = await sut.ListVersionsAsync(PackageFeedType.NuGet, "", "demo", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.Error, result.Outcome);
        Assert.Empty(result.Versions);
    }

    [Fact]
    public async Task RateLimited_KeepsTheRetryDelay_AndNoVersion()
    {
        var sut = Build(HttpStatusCode.TooManyRequests, "");

        var result = await sut.ListVersionsAsync(PackageFeedType.Npm, "", "demo", TestContext.Current.CancellationToken);

        Assert.Equal(PackageResolveOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(90), result.RetryAfter);
        Assert.Empty(result.Versions);
    }

    private sealed class Handler(HttpStatusCode status, string body, List<string> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested.Add(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        }
    }
}
