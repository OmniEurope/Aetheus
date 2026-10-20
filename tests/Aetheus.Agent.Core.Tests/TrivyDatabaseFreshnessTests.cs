// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>A Trivy database built less than 24 h ago is scanned without a refresh; anything older,
/// missing or unreadable keeps the refresh.</summary>
public sealed class TrivyDatabaseFreshnessTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"trivy-cache-{Guid.NewGuid():N}");

    public TrivyDatabaseFreshnessTests() => Directory.CreateDirectory(Path.Combine(_cache, "db"));

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private void WriteMetadata(string json) => File.WriteAllText(Path.Combine(_cache, "db", "metadata.json"), json);

    [Fact]
    public void ADatabaseBuiltWithinTheWindowIsFresh()
    {
        WriteMetadata("""{"Version":2,"NextUpdate":"2026-09-12T06:00:00Z","UpdatedAt":"2026-09-12T00:00:00Z","DownloadedAt":"2026-09-12T01:00:00Z"}""");

        Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero), TrivyDatabaseFreshness.FreshUpdatedAt(_cache, Now));
    }

    [Theory]
    // Built 25 h ago, even though it was downloaded an hour ago: the data is what ages.
    [InlineData("""{"UpdatedAt":"2026-09-11T11:00:00Z","DownloadedAt":"2026-09-12T11:00:00Z"}""")]
    // A timestamp in the future is not trusted.
    [InlineData("""{"UpdatedAt":"2026-09-12T13:00:00Z"}""")]
    [InlineData("""{"DownloadedAt":"2026-09-12T11:00:00Z"}""")]
    [InlineData("""{"UpdatedAt":"not a date"}""")]
    [InlineData("not json")]
    public void AnythingElseKeepsTheRefresh(string metadata)
    {
        WriteMetadata(metadata);

        Assert.Null(TrivyDatabaseFreshness.FreshUpdatedAt(_cache, Now));
    }

    [Fact]
    public void AnEmptyCacheKeepsTheRefresh() => Assert.Null(TrivyDatabaseFreshness.FreshUpdatedAt(_cache, Now));

    [Fact]
    public void Build_AddsTheSkipFlagRightAfterTheSubcommandOnlyForTheCachedTrivyScanners()
    {
        var dependencies = Trivy("trivy-dependencies", cache: "trivy", ["fs", "--scanners", "vuln", "/src"]);
        var iac = Trivy("trivy-iac", cache: null, ["fs", "--scanners", "misconfig", "/src"]);

        var withCache = Arguments(dependencies, skip: true);
        var notAsked = Arguments(dependencies, skip: false);
        var withoutCache = Arguments(iac, skip: true);

        Assert.Equal("fs", withCache[withCache.IndexOf(dependencies.Image!) + 1]);
        Assert.Equal(TrivyDatabaseFreshness.SkipUpdateArgument, withCache[withCache.IndexOf(dependencies.Image!) + 2]);
        Assert.DoesNotContain(TrivyDatabaseFreshness.SkipUpdateArgument, notAsked);
        Assert.DoesNotContain(TrivyDatabaseFreshness.SkipUpdateArgument, withoutCache);
    }

    private static ScannerManifestEntry Trivy(string key, string? cache, List<string> arguments) => new()
    {
        Key = key,
        Image = $"example.invalid/trivy@sha256:{new string('c', 64)}",
        EntryPoint = "trivy",
        Execution = "container",
        CacheDirectoryName = cache,
        Arguments = arguments,
        ReportPath = "report.sarif"
    };

    private List<string> Arguments(ScannerManifestEntry scanner, bool skip) =>
        ScannerContainerProcessBuilder.Build(
            scanner,
            Directory.GetCurrentDirectory(),
            Directory.GetCurrentDirectory(),
            scanner.CacheDirectoryName is null ? null : _cache,
            null,
            new Dictionary<string, string>(),
            null,
            skipDatabaseUpdate: skip).ArgumentList.ToList();
}
