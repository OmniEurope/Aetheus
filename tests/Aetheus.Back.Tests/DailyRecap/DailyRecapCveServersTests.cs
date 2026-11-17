// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>R-41: the recap says, per server, how many CVE of its host system are to handle, only to the readers of that server.</summary>
public sealed class DailyRecapCveServersTests
{
    private static RecapServerCve Server(int id, string name, int high = 0, int critical = 0, int exploited = 0) =>
        new(id, name, new Dictionary<AnalysisSeverity, int>
        {
            [AnalysisSeverity.High] = high,
            [AnalysisSeverity.Critical] = critical
        }.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value), exploited, 0);

    private static DailyRecapReport Report(params RecapServerCve[] servers) =>
        DailyRecapRenderingTests.SampleReport(new HashSet<DailyRecapSection> { DailyRecapSection.Cve }) with
        {
            CveWatch = RecapCveWatch.Empty,
            CveServers = servers,
            CveSources = []
        };

    private static RecapDocument Compose(DailyRecapReport report, string culture = "fr-FR") =>
        DailyRecapComposer.Compose(report, new DailyRecapText(CultureInfo.GetCultureInfo(culture)), "https://app.example.org");

    [Fact]
    public void AServerWithOpenCves_IsListed_EvenOnADayTheProjectsAreQuiet()
    {
        var block = Assert.Single(Compose(Report(Server(1, "vps-1", high: 1, critical: 1, exploited: 1), Server(2, "vps-2"))).Blocks);

        var item = Assert.Single(block.Items);
        Assert.Equal("vps-1", item.Title);
        Assert.Equal("Sur le système de ce serveur il y a 2 CVE à traiter (1 exploitée, 1 critique, 1 élevée)", item.Subtitle);
    }

    [Fact]
    public void TheServerLine_IsWrittenInEnglishToo()
    {
        var block = Assert.Single(Compose(Report(Server(1, "vps-1", high: 2)), "en").Blocks);

        Assert.Equal("On the system of this server there are 2 CVEs to handle (2 high)", Assert.Single(block.Items).Subtitle);
    }

    [Fact]
    public void WithNoServerLine_AndAQuietWatch_TheSectionIsLeftOut()
    {
        Assert.DoesNotContain(Compose(Report(Server(1, "vps-1"))).Blocks, block => block.Title == "Vulnérabilités connues (CVE)");
    }

    [Fact]
    public async Task ARecipient_IsTold_OnlyAboutTheServersTheyMayRead()
    {
        var (scope, authorization) = NewScope();
        authorization.GetAccessibleResourceIdsForUsernameAsync("reader", ResourceType.Server, Permission.Read, Arg.Any<CancellationToken>()).Returns([2]);

        var scoped = await scope.ForAsync(Report(Server(1, "vps-1", high: 1), Server(2, "vps-2", high: 1)), "reader@example.org", TestContext.Current.CancellationToken);

        Assert.Equal([2], scoped.CveServers.Select(server => server.ServerId));
    }

    [Fact]
    public async Task AnAddressWithNoActiveAccount_IsTold_AboutNoServer()
    {
        var (scope, _) = NewScope();

        var scoped = await scope.ForAsync(Report(Server(1, "vps-1", high: 1)), "nobody@example.org", TestContext.Current.CancellationToken);

        Assert.Empty(scoped.CveServers);
    }

    [Fact]
    public async Task TheRepository_CountsTheAffectedFindings_PerServer_AndLeavesTheOnesToVerify()
    {
        await using var db = NewDb();
        var organization = new Organization { Name = "Org", Slug = "org" };
        var server = new Server { Name = "vps-1", Hostname = "vps-1", Organization = organization };
        var critical = new VulnerabilityAdvisory { Source = "UBUNTU", ExternalId = "UBUNTU-CVE-2026-1", Severity = AnalysisSeverity.Critical, SeverityPublished = true, KevAddedAt = DateTime.UtcNow };
        var unverified = new VulnerabilityAdvisory { Source = "UBUNTU", ExternalId = "UBUNTU-CVE-2026-2", Severity = AnalysisSeverity.High, SeverityPublished = true };
        db.ServerVulnerabilityFindings.AddRange(
            new ServerVulnerabilityFinding { Server = server, Advisory = critical, Ecosystem = "Ubuntu:24.04:LTS", PackageName = "openssl", InstalledVersion = "3.0.13-0ubuntu3", Status = VulnerabilityMatchStatus.Affected },
            new ServerVulnerabilityFinding { Server = server, Advisory = unverified, Ecosystem = "Ubuntu:24.04:LTS", PackageName = "apt", InstalledVersion = "2.7.14", Status = VulnerabilityMatchStatus.ToVerify });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var line = Assert.Single(await new DailyRecapRepository(db).GetOpenCvesByServerAsync(TestContext.Current.CancellationToken));

        Assert.Equal("vps-1", line.ServerName);
        Assert.Equal(1, line.Open);
        Assert.Equal(1, line.Exploited);
        Assert.Equal(1, line.BySeverity[AnalysisSeverity.Critical]);
    }

    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 5, 0, 0, TimeSpan.Zero)));

    private static (DailyRecapRecipientScope Scope, IResourceAuthorizationService Authorization) NewScope()
    {
        var db = NewDb();
        db.Users.Add(new User { Username = "reader", Email = "reader@example.org", PasswordHash = "x", IsActive = true });
        db.SaveChanges();
        var authorization = Substitute.For<IResourceAuthorizationService>();
        return (new DailyRecapRecipientScope(new DailyRecapRepository(db), authorization), authorization);
    }
}
