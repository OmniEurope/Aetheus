// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Notifications;
using Aetheus.Shared.Components.VulnerabilityWatch;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// What a runtime advisory becomes once it matches an image or a runtime of a project (PLAN-004 lot 5): the category
/// of its finding, the link it carries, and whether it is announced. Everything goes through the real matching, the
/// real publication and the real announcement on PostgreSQL; only the delivery of the event is recorded. What this
/// does not prove: that a link opens in a browser (the shapes were fetched by hand once), and the delivery of the mail.
/// </summary>
public sealed class CveWatchRuntimeIntegrationTests(PostgresFixture fixture) : CveWatchTestBase(fixture)
{
    private const string Aspnet = "mcr.microsoft.com/dotnet/aspnet";

    private static OsvAdvisoryRecord DotnetAdvisory(string cve) =>
        RuntimeAdvisoryFactory.Create(
            RuntimeProducts.Dotnet,
            $"DOTNET-10.0-{cve}",
            cve,
            [cve],
            $".NET 10.0: {cve}, fixed in 10.0.14",
            (RuntimeAdvisoryFactory.Day(new DateOnly(2026, 10, 1)), RuntimeAdvisoryFactory.Day(new DateOnly(2026, 10, 1))),
            [new RuntimeRange("10.0.0-alpha", "10.0.14")]);

    private async Task<List<AnalysisFinding>> FindingsAsync(int projectId)
    {
        await using var db = NewContext();
        return await db.AnalysisFindings.AsNoTracking().Where(finding => finding.ProjectId == projectId).OrderBy(finding => finding.Id).ToListAsync(Ct);
    }

    /// <summary>A finding filed as a dependency before the runtime ecosystems had their own category must not be swept as
    /// fixed and reopened at every pass: published again, it moves to its category and stays open.</summary>
    [Fact]
    public async Task AFindingFiledUnderDependencies_MovesToContainer_WithoutBeingClosedOrReopenedForGood()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "docker", Aspnet, "10.0.13");
        await ApplyRecordsAsync(DotnetAdvisory("CVE-2099-0001"));
        await PassAsync();
        await using (var db = NewContext())
        {
            var filed = await db.AnalysisFindings.SingleAsync(finding => finding.ProjectId == owner.ProjectId, Ct);
            filed.Category = AnalysisCategory.Dependencies;
            await db.SaveChangesAsync(Ct);
        }

        // Something changes for this project, so the watch publishes it again.
        await AddItemAsync(owner, "docker", Aspnet, "10.0.12");
        await PassAsync();
        await PassAsync();

        var finding = Assert.Single(await FindingsAsync(owner.ProjectId));
        Assert.Equal(AnalysisCategory.Container, finding.Category);
        Assert.Equal(AnalysisFindingStatus.Open, finding.Status);
    }

    private IEnumerable<JsonElement> Events() =>
        Sent.Where(sent => sent.EventType == NotificationEventTypes.CveDetected).Select(sent => JsonDocument.Parse(sent.Json).RootElement.Clone());

    /// <summary>The runtime feeds publish no severity (releases.json carries none). A CVE another advisory of the feed
    /// rates High, the one the project is also exposed to under that name, is as severe for the runtime: it is mailed in the daily
    /// CVE mail (recette R-25). One rated Critical elsewhere leaves at once. One rated lower, or rated by nobody, is not mailed.</summary>
    [Fact]
    public async Task ARuntimeAdvisoryWithNoSeverity_IsAnnounced_OnlyWhenAnotherAdvisoryOfItsCvePublishesAHighOne()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "docker", Aspnet, "10.0.13");
        await ApplyRecordsAsync(DotnetAdvisory("CVE-2099-0001"), DotnetAdvisory("CVE-2099-0002"), DotnetAdvisory("CVE-2099-0003"), DotnetAdvisory("CVE-2099-0004"));
        await ApplyAsync(
            Advisory("GHSA-high", "Sibling", UpToFix, severity: "HIGH", cve: "CVE-2099-0001"),
            Advisory("GHSA-medium", "Sibling", UpToFix, severity: "MODERATE", cve: "CVE-2099-0002"),
            Advisory("GHSA-critical", "Sibling", UpToFix, severity: "CRITICAL", cve: "CVE-2099-0004"));
        await SetRecapHourAsync("08:00");

        await PassAsync();

        // The four CVEs matched the image; only the one rated Critical elsewhere left at once, the one rated High waits.
        Assert.Equal(4, (await MatchesAsync(owner.ProjectId)).Count);
        Assert.Equal(["CVE-2099-0004"], Listed(Assert.Single(Events())));

        await NightAsync();

        // The daily mail tells only the High one, new; the Critical one already left, the Medium and the unrated never do.
        Assert.Equal(2, Events().Count());
        Assert.Equal(["CVE-2099-0001"], Listed(Events().Last()));
    }

    private static IEnumerable<string> Listed(JsonElement payload) =>
        payload.GetProperty("Projects").EnumerateArray()
            .SelectMany(project => project.GetProperty("Vulnerabilities").EnumerateArray())
            .Select(item => item.GetProperty("RuleId").GetString()!);
}
