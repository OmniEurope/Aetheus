// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Analysis;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// What the daily recap reads of the vulnerability watch (PLAN-004, lot 3, point 15), on PostgreSQL: matches made
/// by the real parser, feed repository, matching and publication of the findings, then counted by the recap's own
/// query. Nothing here proves the e-mail is delivered; the rendering is covered by the composer tests.
/// </summary>
public sealed class DailyRecapCveWatchIntegrationTests(PostgresFixture fixture) : CveWatchTestBase(fixture)
{
    private static readonly DateTime DayStart = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DayEnd = DayStart.AddDays(1);

    private async Task<RecapCveWatch> ReadAsync(DateTime startUtc, DateTime endUtc)
    {
        await using var db = NewContext();
        return await new DailyRecapRepository(db).GetCveWatchAsync(startUtc, endUtc, Ct);
    }

    [Fact]
    public async Task TheRecap_CountsOpenMatchesBySeverity_ExploitedUnratedAndToVerify_AndWhatAppearedOrClosedInTheDay()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "libcritical", "1.0.0");
        await AddItemAsync(owner, "libhigh", "1.0.0");
        await AddItemAsync(owner, "libmedium", "1.0.0");
        await AddItemAsync(owner, "libexploited", "1.0.0");
        await AddItemAsync(owner, "libunrated", "1.0.0");
        await AddItemAsync(owner, "libunreadable", "1.0.0");
        await AddItemAsync(owner, "libaccepted", "1.0.0");
        var fixedLater = await AddItemAsync(owner, "libfixed", "1.0.0");
        await ApplyAsync(
            Advisory("GHSA-crit", "LibCritical", UpToFix, severity: "CRITICAL", cve: "CVE-2099-0001"),
            Advisory("GHSA-high", "LibHigh", UpToFix, severity: "HIGH", cve: "CVE-2099-0002"),
            Advisory("GHSA-med", "LibMedium", UpToFix, severity: "MODERATE", cve: "CVE-2099-0003"),
            Advisory("GHSA-kev", "LibExploited", UpToFix, severity: "HIGH", cve: "CVE-2099-0004"),
            Advisory("GHSA-none", "LibUnrated", UpToFix, severity: null, cve: "CVE-2099-0005"),
            Advisory("GHSA-bad", "LibUnreadable", Unreadable, cve: "CVE-2099-0006"),
            Advisory("GHSA-acc", "LibAccepted", UpToFix, severity: "CRITICAL", cve: "CVE-2099-0007"),
            Advisory("GHSA-fix", "LibFixed", UpToFix, severity: "HIGH", cve: "CVE-2099-0008"));
        await using (var db = NewContext())
        {
            (await db.VulnerabilityAdvisories.SingleAsync(advisory => advisory.ExternalId == "GHSA-kev", Ct)).KevAddedAt =
                new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync(Ct);
        }

        await PassAsync();
        await using (var db = NewContext())
        {
            var accepted = await db.VulnerabilityMatches.Where(match => match.PackageName == "libaccepted").Select(match => match.AnalysisFinding!).SingleAsync(Ct);
            accepted.Status = AnalysisFindingStatus.Accepted;
            await db.SaveChangesAsync(Ct);
        }

        // The next day, one package is bumped to its fixed version: its match closes.
        AdvanceClock(TimeSpan.FromDays(1));
        await MoveItemAsync(owner, fixedLater, "libfixed", "2.0.0");
        await PassAsync();

        var firstDay = await ReadAsync(DayStart, DayEnd);
        Assert.Equal(1, firstDay.OpenBySeverity[AnalysisSeverity.Critical]);
        Assert.Equal(2, firstDay.OpenBySeverity[AnalysisSeverity.High]);
        Assert.Equal(1, firstDay.OpenBySeverity[AnalysisSeverity.Medium]);
        Assert.Equal(1, firstDay.OpenExploited);
        Assert.Equal(1, firstDay.OpenUnrated);
        Assert.Equal(1, firstDay.OpenToVerify);
        Assert.Equal(5, firstDay.OpenTotal);
        // Eight matches appeared on the 1st (seven affected, one to verify); only the affected ones count as new.
        Assert.Equal(7, firstDay.NewInDay);
        Assert.Equal(0, firstDay.ClosedInDay);

        var secondDay = await ReadAsync(DayEnd, DayEnd.AddDays(1));
        Assert.Equal(0, secondDay.NewInDay);
        Assert.Equal(1, secondDay.ClosedInDay);
        Assert.Equal(5, secondDay.OpenTotal);
        Assert.False(secondDay.IsQuiet);
    }

}
