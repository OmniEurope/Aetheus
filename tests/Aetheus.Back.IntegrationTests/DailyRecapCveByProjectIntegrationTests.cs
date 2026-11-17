// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Analysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-25, on PostgreSQL: the recap says, per project, how many CVEs are still to handle, and a recipient
/// reads only the projects their role reaches, never those of another organization. Matches come from the real
/// parser, matching and publication; the scope is the real resource authorization. Nothing here proves a mail
/// reaches an inbox.
/// </summary>
public sealed class DailyRecapCveByProjectIntegrationTests(PostgresFixture fixture) : CveWatchTestBase(fixture)
{
    private static readonly DateTime DayStart = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task SeedPassedAsync(
        (int OrganizationId, int ProjectId, int InventoryId) alpha, (int OrganizationId, int ProjectId, int InventoryId) beta)
    {
        await AddItemAsync(alpha, "libcritical", "1.0.0");
        await AddItemAsync(alpha, "libhigh", "1.0.0");
        await AddItemAsync(beta, "libsecret", "1.0.0");
        await ApplyAsync(
            Advisory("GHSA-crit", "LibCritical", UpToFix, severity: "CRITICAL", cve: "CVE-2099-0001"),
            Advisory("GHSA-high", "LibHigh", UpToFix, severity: "HIGH", cve: "CVE-2099-0002"),
            Advisory("GHSA-sec", "LibSecret", UpToFix, severity: "HIGH", cve: "CVE-2099-0003"));
        await PassAsync();
    }

    private async Task<string> ReaderOfAsync(int organizationId, string email)
    {
        await using var db = NewContext();
        var role = new Role { Name = "RecapReader", Description = "d", OrganizationId = organizationId };
        role.ResourcePermissions.Add(new ResourcePermission { ResourceType = ResourceType.Project, ResourceId = null, Permission = Permission.Read });
        db.Roles.Add(role);
        var user = new User { Username = "reader", Email = email, PasswordHash = "x", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = user.Id, OrganizationId = organizationId, RoleId = role.Id });
        db.OrganizationMembers.Add(new OrganizationMember { UserId = user.Id, OrganizationId = organizationId, Role = OrganizationRole.Member });
        await db.SaveChangesAsync(Ct);
        return user.Username;
    }

    private async Task<DailyRecapReport> CollectAsync(AppDbContext db) => new()
    {
        Day = new DateOnly(2026, 11, 1),
        GeneratedAtUtc = DayStart.AddHours(8),
        TimeZone = TimeZoneInfo.Utc,
        Sections = new HashSet<DailyRecapSection> { DailyRecapSection.Cve },
        CveWatch = await new DailyRecapRepository(db).GetCveWatchAsync(DayStart, DayStart.AddDays(1), Ct),
        CveProjects = await new DailyRecapRepository(db).GetOpenCvesByProjectAsync(Ct)
    };

    [Fact]
    public async Task TheRecapOfARecipient_ListsTheOpenCountsOfTheProjectsTheyRead_AndNotThoseOfAnotherOrganization()
    {
        await ResetAndMigrateAsync();
        var alpha = await SeedProjectAsync("Alpha");
        var beta = await SeedProjectAsync("Beta");
        await SeedPassedAsync(alpha, beta);
        await ReaderOfAsync(alpha.OrganizationId, "reader@example.test");
        await using var db = NewContext();
        using var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var scope = new DailyRecapRecipientScope(new DailyRecapRepository(db),
            new ResourceAuthorizationService(new PermissionRepository(db), cache, new AuthzCacheEvictor()));
        var report = await CollectAsync(db);
        Assert.Equal(["Alpha", "Beta"], report.CveProjects.Select(project => project.ProjectName));

        var scoped = await scope.ForAsync(report, "Reader@Example.test", Ct);
        var block = Assert.Single(DailyRecapComposer.Compose(scoped, new DailyRecapText(CultureInfo.GetCultureInfo("fr-FR")), null).Blocks);

        var line = Assert.Single(block.Items);
        Assert.Equal("Alpha", line.Title);
        Assert.Equal("Sur ce projet il y a 2 CVE à traiter (1 critique, 1 élevée)", line.Subtitle);
        Assert.DoesNotContain(block.Items, item => item.Title == "Beta");

        var stranger = await scope.ForAsync(report, "nobody@example.test", Ct);
        Assert.Empty(stranger.CveProjects);
    }

    [Fact]
    public async Task AProjectWhoseCvesAreAllAcceptedOrFixed_IsNotListed()
    {
        await ResetAndMigrateAsync();
        var alpha = await SeedProjectAsync("Alpha");
        var beta = await SeedProjectAsync("Beta");
        await SeedPassedAsync(alpha, beta);
        await using (var db = NewContext())
        {
            var finding = await db.VulnerabilityMatches.Where(match => match.PackageName == "libsecret")
                .Select(match => match.AnalysisFinding!).SingleAsync(Ct);
            finding.Status = AnalysisFindingStatus.Accepted;
            await db.SaveChangesAsync(Ct);
        }

        await using var read = NewContext();
        var projects = await new DailyRecapRepository(read).GetOpenCvesByProjectAsync(Ct);

        Assert.Equal(["Alpha"], projects.Select(project => project.ProjectName));
        Assert.Equal(2, projects[0].Open);
    }
}
