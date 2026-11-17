// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// <c>GET api/analysis/cve-watch</c> through the real HTTP pipeline, JWT included, on real PostgreSQL (PLAN-004,
/// lot 3, point 13). The matches are inserted as the watch stores them: this proves who sees what, how the page
/// is cut and in which order, not the matching itself.
/// </summary>
public sealed class CveWatchHttpIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed record Seeded(int OrganizationId, int FirstProjectId, int SecondProjectId);

    private static int _advisories;

    private static string Url(int organizationId, string query = "") => $"/api/analysis/cve-watch?organizationId={organizationId}{query}";

    [Fact]
    public async Task AMemberOfAnOrganization_SeesItsReadableProjects_AndNothingOfTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "a");
        var b = await SeedOrganizationAsync(factory, "b");
        var allOfA = await IntegrationUsers.CreateAsync(factory.Services, "watch-all",
            [new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)], a.OrganizationId);
        var onlyFirstOfA = await IntegrationUsers.CreateAsync(factory.Services, "watch-one",
            [new IntegrationUsers.Grant(ResourceType.Project, a.FirstProjectId, Permission.Read)], a.OrganizationId);
        var noneOfA = await IntegrationUsers.CreateAsync(factory.Services, "watch-none", [], a.OrganizationId);
        // A grant that names a project of another organization gives nothing: the role is held for A only.
        var namesB = await IntegrationUsers.CreateAsync(factory.Services, "watch-names-b",
            [new IntegrationUsers.Grant(ResourceType.Project, b.FirstProjectId, Permission.Read)], a.OrganizationId);
        using var allClient = await IntegrationUsers.LoginClientAsync(factory, allOfA.Username);
        using var oneClient = await IntegrationUsers.LoginClientAsync(factory, onlyFirstOfA.Username);
        using var noneClient = await IntegrationUsers.LoginClientAsync(factory, noneOfA.Username);
        using var namesBClient = await IntegrationUsers.LoginClientAsync(factory, namesB.Username);

        // The positive control: a reader of every project of A sees what A holds.
        var everything = await ReadAsync(allClient, a.OrganizationId);
        Assert.Equal(["a-first", "a-second"], everything.Items.Select(item => item.ProjectName).Order());
        Assert.All(everything.Items, item => Assert.Equal(a.OrganizationId, item.OrganizationId));

        var limited = Assert.Single((await ReadAsync(oneClient, a.OrganizationId)).Items);
        Assert.Equal(a.FirstProjectId, limited.ProjectId);
        Assert.Empty((await ReadAsync(noneClient, a.OrganizationId)).Items);
        Assert.Empty((await ReadAsync(namesBClient, a.OrganizationId)).Items);

        // Another organization is refused outright, whatever the grants: never a page, never its data.
        foreach (var client in new[] { allClient, oneClient, noneClient, namesBClient })
        {
            var foreign = await client.GetAsync(Url(b.OrganizationId), ct);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            var body = await foreign.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("b-first", body, StringComparison.Ordinal);
            Assert.DoesNotContain("CVE-B", body, StringComparison.Ordinal);
        }

        // Naming a project of B inside A's organization finds nothing of B either.
        Assert.Empty((await ReadAsync(allClient, a.OrganizationId, $"&projectId={b.FirstProjectId}")).Items);

        // A site Admin reads either organization.
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        Assert.Equal(["b-first", "b-second"], (await ReadAsync(admin, b.OrganizationId)).Items.Select(item => item.ProjectName).Order());
        Assert.Equal(2, (await ReadAsync(admin, a.OrganizationId)).TotalCount);
    }

    [Fact]
    public async Task ThePage_IsCut_ExploitedFirstThenTheWorst_AndClosedMatchesAreAskedFor()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var seeded = await SeedOrganizationAsync(factory, "page");
        await AddMatchAsync(factory, seeded, seeded.FirstProjectId, "lib-medium", AnalysisSeverity.Medium);
        await AddMatchAsync(factory, seeded, seeded.FirstProjectId, "lib-critical", AnalysisSeverity.Critical);
        await AddMatchAsync(factory, seeded, seeded.FirstProjectId, "lib-exploited", AnalysisSeverity.High, exploited: true);
        await AddMatchAsync(factory, seeded, seeded.FirstProjectId, "lib-high", AnalysisSeverity.High);
        await AddMatchAsync(factory, seeded, seeded.FirstProjectId, "lib-closed", AnalysisSeverity.Critical, closed: true);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        // Open ones only by default: the page holds the exploited one, then the worst.
        var first = await ReadAsync(admin, seeded.OrganizationId, "&pageSize=2&projectId=" + seeded.FirstProjectId);
        Assert.Equal(["lib-exploited", "lib-critical"], first.Items.Select(item => item.PackageName));
        Assert.Equal(4, first.TotalCount);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(1, first.Page);
        var second = await ReadAsync(admin, seeded.OrganizationId, "&pageSize=2&page=2&projectId=" + seeded.FirstProjectId);
        Assert.Equal(["lib-high", "lib-medium"], second.Items.Select(item => item.PackageName));
        Assert.NotNull(first.Items[0].ExploitedSince);
        Assert.Equal(VulnerabilityMatchStatus.Affected, first.Items[0].Status);
        Assert.Equal("2.0.0", first.Items[0].FixedVersion);

        var withClosed = await ReadAsync(admin, seeded.OrganizationId, "&includeClosed=true&pageSize=10&projectId=" + seeded.FirstProjectId);
        Assert.Equal(5, withClosed.TotalCount);
        Assert.NotNull(withClosed.Items.Single(item => item.PackageName == "lib-closed").ClosedAt);
    }

    private static async Task<PaginatedResult<CveWatchMatchDto>> ReadAsync(HttpClient client, int organizationId, string query = "")
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.GetAsync(Url(organizationId, query), ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return await response.Content.ReadFromJsonAsync<PaginatedResult<CveWatchMatchDto>>(IntegrationJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("The page came back empty.");
    }

    /// <summary>An organization with two projects, each holding one open match, the advisory named after its project.</summary>
    private static async Task<Seeded> SeedOrganizationAsync(AetheusWebApplicationFactory factory, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        Seeded seeded;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var organization = new Organization { Name = $"watch-{label}-{Guid.NewGuid():N}", Slug = $"w{Guid.NewGuid():N}"[..20] };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(ct);
            var first = new Project { Name = $"{label}-first", OrganizationId = organization.Id };
            var second = new Project { Name = $"{label}-second", OrganizationId = organization.Id };
            db.Projects.AddRange(first, second);
            await db.SaveChangesAsync(ct);
            seeded = new Seeded(organization.Id, first.Id, second.Id);
        }

        if (label is "a" or "b")
        {
            await AddMatchAsync(factory, seeded, seeded.FirstProjectId, $"{label}-lib-first", AnalysisSeverity.High, cve: $"CVE-{label.ToUpperInvariant()}-1");
            await AddMatchAsync(factory, seeded, seeded.SecondProjectId, $"{label}-lib-second", AnalysisSeverity.High, cve: $"CVE-{label.ToUpperInvariant()}-2");
        }

        return seeded;
    }

    private static async Task AddMatchAsync(
        AetheusWebApplicationFactory factory, Seeded organization, int projectId, string package, AnalysisSeverity severity,
        bool exploited = false, bool closed = false, string? cve = null)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var advisory = new VulnerabilityAdvisory
        {
            Source = "GHSA",
            ExternalId = $"GHSA-{Guid.NewGuid():N}"[..24],
            PrimaryCveId = cve ?? $"CVE-2099-{Interlocked.Increment(ref _advisories):0000}",
            Severity = severity,
            SeverityPublished = true,
            ModifiedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            KevAddedAt = exploited ? new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc) : null
        };
        db.VulnerabilityAdvisories.Add(advisory);
        await db.SaveChangesAsync(ct);
        db.VulnerabilityMatches.Add(new VulnerabilityMatch
        {
            OrganizationId = organization.OrganizationId,
            ProjectId = projectId,
            AdvisoryId = advisory.Id,
            Ecosystem = "NuGet",
            PackageName = package,
            Version = "1.0.0",
            FixedVersion = "2.0.0",
            Status = VulnerabilityMatchStatus.Affected,
            FirstMatchedAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
            ClosedAt = closed ? new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc) : null
        });
        await db.SaveChangesAsync(ct);
    }
}
