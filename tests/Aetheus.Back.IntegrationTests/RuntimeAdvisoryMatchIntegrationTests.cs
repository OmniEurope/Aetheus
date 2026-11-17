// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The runtime advisories of the watch on PostgreSQL (PLAN-004 lot 5, points 1 to 5), end to end and with no pipeline:
/// the real importers read the real documents recorded on 2026-11-01 from Microsoft, nodejs.org, php.net and endoflife.date
/// (tests/fixtures/runtime-advisories, cut to a few channels and releases), the advisories go through the real feed
/// repository, and the real matching crosses them with inventory items the way a Dockerfile or a manifest gives them.
/// The only double is the HTTP exchange. What this does not prove: the reach of the real sources from a server
/// (the documents were fetched by hand once), and the notification, which the lot 3 tests cover.
/// </summary>
public sealed class RuntimeAdvisoryMatchIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string Aspnet = "mcr.microsoft.com/dotnet/aspnet";
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly StubFeedHandler _handler = new();
    private readonly ManualTimeProvider _clock = new(Now);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "runtime-advisories", name));

    private static VulnerabilityWatchOptions Options() => new()
    {
        DotnetReleasesBaseUrl = "https://feeds.test/dotnet",
        NodeIndexUrl = "https://feeds.test/node/index.json",
        PhpBaseUrl = "https://feeds.test/php",
        EndOfLifeBaseUrl = "https://feeds.test/eol",
        EndOfLifeProducts = ["dotnet", "nodejs", "php", "python"],
        BatchRecords = 200
    };

    private VulnerabilityFeedClient Client() => new(new StubFactory(_handler), Microsoft.Extensions.Options.Options.Create(Options()));

    private void MapSources()
    {
        _handler.Map("https://feeds.test/dotnet/releases-index.json", Fixture("dotnet-releases-index.json"));
        foreach (var channel in new[] { "11.0", "10.0", "9.0", "8.0" })
            _handler.Map($"https://feeds.test/dotnet/{channel}/releases.json", Fixture($"dotnet-{channel}-releases.json"));
        _handler.Map("https://feeds.test/node/index.json", Fixture("node-index.json"));
        foreach (var major in new[] { 7, 8 })
        {
            _handler.Map($"https://feeds.test/php/releases/index.php?json&version={major}&max=1000", Fixture($"php-{major}-releases.json"));
            _handler.Map($"https://feeds.test/php/ChangeLog-{major}.php", Fixture($"php-{major}-changelog.html"));
        }

        foreach (var product in new[] { "dotnet", "nodejs", "php", "python" })
            _handler.Map($"https://feeds.test/eol/{product}.json", Fixture($"eol-{product}.json"));
    }

    private async Task LoadSourcesAsync()
    {
        // Every test starts from the pristine schema: the advisories another class left on this database (a runtime CVE
        // seeded by hand for the same image) would otherwise be matched too.
        await ResetAndMigrateAsync();
        MapSources();
        await using var db = NewContext();
        var repository = new VulnerabilityFeedRepository(db);
        var options = Microsoft.Extensions.Options.Options.Create(Options());
        async Task<VulnerabilityFeedState> State(string source) => await repository.GetOrCreateStateAsync(source, Ct);
        foreach (var product in options.Value.EffectiveEndOfLifeProducts)
            await new EndOfLifeFeedImporter(Client(), repository, options).SyncAsync(product, await State("eol:" + product), Ct);
        await new DotnetReleaseFeedImporter(Client(), repository, options, _clock).SyncAsync(await State("runtime:dotnet"), Ct);
        await new NodeReleaseFeedImporter(Client(), repository, options, _clock).SyncAsync(await State("runtime:nodejs"), Ct);
        await new PhpReleaseFeedImporter(Client(), repository, options, _clock).SyncAsync(await State("runtime:php"), Ct);
        await new EndOfLifeAdvisoryImporter(new EndOfLifeCycleRepository(db), repository, options, _clock).SyncAsync(await State("eol-advisories"), Ct);
    }

    private async Task MatchAsync()
    {
        await using var db = NewContext();
        var service = new VulnerabilityMatchService(new VulnerabilityMatchRepository(db), _clock, NullLogger<VulnerabilityMatchService>.Instance);
        await service.MatchChangedAdvisoriesAsync(Ct);
        await service.MatchChangedInventoriesAsync(null, Ct);
    }

    /// <summary>One project whose inventory holds these items, as a Dockerfile (<c>docker</c>) or a manifest (<c>runtime</c>) gives them.</summary>
    private async Task<int> SeedProjectAsync(string name, params (string Ecosystem, string Package, string Version)[] items)
    {
        await using var db = NewContext();
        var organization = new Organization { Name = name, Slug = name.ToLowerInvariant(), Description = "d" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = name, Description = "d", OrganizationId = organization.Id };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        var inventory = new ProjectInventory
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            Origin = ProjectInventoryOrigin.Repository,
            ReadAt = Now.UtcDateTime,
            ChangedAt = Now.UtcDateTime
        };
        db.ProjectInventories.Add(inventory);
        await db.SaveChangesAsync(Ct);
        db.ProjectInventoryItems.AddRange(items.Select(item => new ProjectInventoryItem
        {
            OrganizationId = organization.Id,
            ProjectId = project.Id,
            ProjectInventoryId = inventory.Id,
            Ecosystem = item.Ecosystem,
            PackageName = item.Package,
            Version = item.Version,
            PackageUrl = $"pkg:{item.Ecosystem}/{item.Package}@{item.Version}",
            Kind = item.Ecosystem == "runtime" ? ProjectInventoryItemKind.Runtime : ProjectInventoryItemKind.BaseImage
        }));
        await db.SaveChangesAsync(Ct);
        return project.Id;
    }

    private async Task<List<(string Advisory, string Package, string Version, VulnerabilityMatchStatus Status, string? Fixed)>> MatchesAsync(int projectId)
    {
        await using var db = NewContext();
        var rows = await db.VulnerabilityMatches.AsNoTracking().Include(match => match.Advisory)
            .Where(match => match.ProjectId == projectId && match.ClosedAt == null).ToListAsync(Ct);
        return [.. rows.Select(match => (match.Advisory.ExternalId, match.PackageName, match.Version, match.Status, match.FixedVersion)).Order()];
    }

    [Fact]
    public async Task AnAspnetImageOfTheFixedRelease_RaisesTheCveOfThePreviousOne_AndTheNextReleaseRaisesNothing()
    {
        await LoadSourcesAsync();
        var behind = await SeedProjectAsync("Behind", ("docker", Aspnet, "10.0.13"));
        var current = await SeedProjectAsync("Current", ("docker", Aspnet, "10.0.14"));
        var floating = await SeedProjectAsync("Floating", ("docker", Aspnet, "10.0"));

        await MatchAsync();

        var matches = await MatchesAsync(behind);
        Assert.Equal(
            [
                ("DOTNET-10.0-CVE-2026-111497", Aspnet, "10.0.13", VulnerabilityMatchStatus.Affected, "10.0.14"),
                ("DOTNET-10.0-CVE-2026-114148", Aspnet, "10.0.13", VulnerabilityMatchStatus.Affected, "10.0.14")
            ],
            matches);
        Assert.Empty(await MatchesAsync(current));
        // A tag that states no patch is read as the newest of its cycle: only the SBOM of a built image can say more.
        Assert.Empty(await MatchesAsync(floating));
    }

    [Fact]
    public async Task ARuntimeOrImageOfACycleThatEnded_IsRaisedAsEndOfLife_WithNoPipeline()
    {
        await LoadSourcesAsync();
        var old = await SeedProjectAsync(
            "Old",
            ("runtime", "nodejs", "20"), ("docker", "node", "20-alpine"), ("docker", "python", "3.10-slim"), ("runtime", "dotnet", "7.0.100"));
        var supported = await SeedProjectAsync("Supported", ("docker", "node", "22-alpine"), ("docker", "python", "3.11"), ("docker", Aspnet, "8.0.5"));

        await MatchAsync();

        var matches = await MatchesAsync(old);
        Assert.Contains(("EOL-nodejs-20", "nodejs", "20", VulnerabilityMatchStatus.Affected, null), matches);
        Assert.Contains(("EOL-nodejs-20", "node", "20-alpine", VulnerabilityMatchStatus.Affected, null), matches);
        Assert.Contains(("EOL-python-3.10", "python", "3.10-slim", VulnerabilityMatchStatus.Affected, null), matches);
        // The SDK of a global.json is not a release of the runtime, so it carries no CVE, but it tells the cycle.
        Assert.Equal([("EOL-dotnet-7", "dotnet", "7.0.100", VulnerabilityMatchStatus.Affected, null)],
            matches.Where(match => match.Package == "dotnet").ToList());
        // .NET 8 ends on 2026-11-10: nothing about its end of life on 2026-11-01, only the CVEs its image is behind on.
        Assert.DoesNotContain(await MatchesAsync(supported), match => match.Advisory.StartsWith("EOL-", StringComparison.Ordinal));
    }

    private sealed class StubFeedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _bodies = new(StringComparer.Ordinal);

        public void Map(string url, string body) => _bodies[url] = System.Text.Encoding.UTF8.GetBytes(body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_bodies.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
