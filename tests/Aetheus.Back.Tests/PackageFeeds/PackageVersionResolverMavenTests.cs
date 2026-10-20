// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.PackageFeeds;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.PackageFeeds;

/// <summary>Maven resolution against a repository served by a stub: the artifact metadata, then for a
/// selected snapshot its own metadata. The requested URLs are recorded so the repository path (Maven
/// keeps the <c>/maven2</c> path of its root) is checked too.</summary>
public sealed class PackageVersionResolverMavenTests
{
    private const string Root = "https://repo.example.org/maven2";
    private const string ArtifactMetadata = Root + "/org/example/demo/maven-metadata.xml";

    private const string Artifact = """
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata>
          <groupId>org.example</groupId><artifactId>demo</artifactId>
          <versioning>
            <latest>2.1-SNAPSHOT</latest><release>2.0</release>
            <versions>
              <version>1.0</version><version>1.10</version><version>1.9</version>
              <version>2.0</version><version>2.1-SNAPSHOT</version>
            </versions>
            <lastUpdated>20261017120000</lastUpdated>
          </versioning>
        </metadata>
        """;

    private const string Snapshot = """
        <metadata modelVersion="1.1.0">
          <groupId>org.example</groupId><artifactId>demo</artifactId><version>2.1-SNAPSHOT</version>
          <versioning>
            <snapshot><timestamp>20261017.101500</timestamp><buildNumber>3</buildNumber></snapshot>
            <snapshotVersions>
              <snapshotVersion><extension>jar</extension><value>2.1-20261017.101500-3</value></snapshotVersion>
            </snapshotVersions>
          </versioning>
        </metadata>
        """;

    private readonly List<string> _requested = [];

    private PackageVersionResolver Build(Dictionary<string, string> responses)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("package-feeds").Returns(_ => new HttpClient(new RoutingHandler(responses, _requested)));
        return new PackageVersionResolver(factory, NullLogger<PackageVersionResolver>.Instance, TimeProvider.System);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("org.example:demo", "2.0")]
    [InlineData("org.example:demo:RELEASE", "2.0")]
    [InlineData("org.example:demo:[1.0,2.0)", "1.10")]
    [InlineData("org.example:demo:(,1.9]", "1.9")]
    [InlineData("org.example:demo:1.9", "1.9")]
    public async Task Resolve_SelectsLikeMaven(string name, string expected)
    {
        var sut = Build(new() { [ArtifactMetadata] = Artifact });
        var result = await sut.ResolveLatestAsync(PackageFeedType.Maven, Root + "/", name, Ct);

        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(expected, result.LatestVersion);
        Assert.Equal([ArtifactMetadata], _requested);
    }

    [Fact]
    public async Task Resolve_Latest_ResolvesTheSnapshotToItsNewestTimestampedBuild()
    {
        var sut = Build(new()
        {
            [ArtifactMetadata] = Artifact,
            [Root + "/org/example/demo/2.1-SNAPSHOT/maven-metadata.xml"] = Snapshot
        });
        var result = await sut.ResolveLatestAsync(PackageFeedType.Maven, Root, "org.example:demo:LATEST", Ct);

        Assert.Equal(PackageResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("2.1-20261017.101500-3", result.LatestVersion);
        Assert.Equal(new DateTime(2026, 10, 17, 10, 15, 0, DateTimeKind.Utc), result.PublishedAt);
    }

    [Fact]
    public async Task Resolve_WithoutReleaseElement_PicksTheHighestNonSnapshot()
    {
        const string bare = """
            <metadata><versioning><versions><version>1.9</version><version>1.10</version><version>2.0-SNAPSHOT</version></versions></versioning></metadata>
            """;
        var sut = Build(new() { [ArtifactMetadata] = bare });
        var result = await sut.ResolveLatestAsync(PackageFeedType.Maven, Root, "org.example:demo", Ct);
        Assert.Equal("1.10", result.LatestVersion);
    }

    [Fact]
    public async Task Resolve_BlankUpstream_UsesMavenCentral()
    {
        var central = PackageRegistryDefaults.Maven + "/org/example/demo/maven-metadata.xml";
        var sut = Build(new() { [central] = Artifact });
        var result = await sut.ResolveLatestAsync(PackageFeedType.Maven, "", "org.example:demo", Ct);
        Assert.Equal("2.0", result.LatestVersion);
        Assert.Equal([central], _requested);
    }

    [Theory]
    [InlineData("demo")]
    [InlineData("org.example:")]
    [InlineData("../etc:passwd")]
    [InlineData("org/example:demo")]
    public async Task Resolve_InvalidCoordinates_AreNotFoundWithoutAnyRequest(string name)
    {
        var sut = Build([]);
        var result = await sut.ResolveLatestAsync(PackageFeedType.Maven, Root, name, Ct);
        Assert.Equal(PackageResolveOutcome.NotFound, result.Outcome);
        Assert.Empty(_requested);
    }

    [Fact]
    public async Task Resolve_UnknownArtifact_IsNotFound_AndNoMatch_IsNotFound()
    {
        var missing = await Build([]).ResolveLatestAsync(PackageFeedType.Maven, Root, "org.example:demo", Ct);
        Assert.Equal(PackageResolveOutcome.NotFound, missing.Outcome);

        var noMatch = await Build(new() { [ArtifactMetadata] = Artifact })
            .ResolveLatestAsync(PackageFeedType.Maven, Root, "org.example:demo:[5.0,)", Ct);
        Assert.Equal(PackageResolveOutcome.NotFound, noMatch.Outcome);
    }

    [Theory]
    [InlineData("<metadata><versioning>")]
    [InlineData("<!DOCTYPE m [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><metadata>&x;</metadata>")]
    public async Task Resolve_MalformedOrHostileMetadata_IsAnError_NeverAVersion(string body)
    {
        var result = await Build(new() { [ArtifactMetadata] = body })
            .ResolveLatestAsync(PackageFeedType.Maven, Root, "org.example:demo", Ct);
        Assert.Equal(PackageResolveOutcome.Error, result.Outcome);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task Resolve_MalformedRange_IsAnError()
    {
        var result = await Build(new() { [ArtifactMetadata] = Artifact })
            .ResolveLatestAsync(PackageFeedType.Maven, Root, "org.example:demo:[2.0,1.0]", Ct);
        Assert.Equal(PackageResolveOutcome.Error, result.Outcome);
    }

    private sealed class RoutingHandler(Dictionary<string, string> responses, List<string> requested) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            requested.Add(url);
            return Task.FromResult(responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
