// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data;
using Aetheus.Shared.Components.Analysis;
using Aetheus.Shared.Components.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The immediate announcement of the vulnerability watch on PostgreSQL (PLAN-004, recette R-25 and R-26): after a pass,
/// ONE <c>analysis.cve.detected</c> event for all the projects, for the NEW severe matches only (exploited or Critical);
/// a published High waits for the nightly mail. Never repeated for a match already announced, repeated for a match that
/// came back. Everything goes through the real matching, the real publication and the real announcement; only the
/// delivery of the event is recorded instead of made, so what these tests do not prove is the recipients, the SMTP
/// delivery and the inbox (the narrowing per recipient is proved on its own in <c>UserNotificationDigestTests</c>).
/// </summary>
public sealed class CveWatchAnnouncementIntegrationTests(PostgresFixture fixture) : CveWatchTestBase(fixture)
{
    private static readonly DateTime FirstPassAt = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private IEnumerable<JsonElement> Events() =>
        Sent.Where(sent => sent.EventType == NotificationEventTypes.CveDetected).Select(sent => JsonDocument.Parse(sent.Json).RootElement.Clone());

    private static JsonElement ProjectOf(JsonElement payload, int projectId) =>
        payload.GetProperty("Projects").EnumerateArray().Single(project => project.GetProperty("ProjectId").GetInt32() == projectId);

    private static string[] Listed(JsonElement project) =>
        [.. project.GetProperty("Vulnerabilities").EnumerateArray().Select(item => item.GetProperty("RuleId").GetString() + " " + item.GetProperty("PackageName").GetString())];

    [Fact]
    public async Task SeveralProjects_AreToldInOneEvent_NotOnePerProject_TheWorstFirst()
    {
        await ResetAndMigrateAsync();
        var alpha = await SeedProjectAsync("Alpha");
        var beta = await SeedProjectAsync("Beta");
        await AddItemAsync(alpha, "liba", "1.0.0");
        await AddItemAsync(alpha, "libb", "1.0.0");
        await AddItemAsync(beta, "liba", "1.0.0");
        await ApplyAsync(
            Advisory("GHSA-1", "LibA", UpToFix, severity: "CRITICAL"),
            Advisory("GHSA-2", "LibB", UpToFix, severity: "CRITICAL", cve: "CVE-2099-0002"));

        await PassAsync();

        // R-26: three matches in two projects leave in ONE event, not two.
        var only = Assert.Single(Events());
        Assert.False(only.GetProperty("Digest").GetBoolean());
        Assert.Equal(2, only.GetProperty("Projects").GetArrayLength());
        var forAlpha = ProjectOf(only, alpha.ProjectId);
        Assert.Equal("Alpha", forAlpha.GetProperty("ProjectName").GetString());
        Assert.Equal(alpha.OrganizationId, forAlpha.GetProperty("OrganizationId").GetInt32());
        // R-8: liba is the same vulnerability in both projects, so it is told once in Shared; Alpha keeps only libb.
        Assert.Equal(1, forAlpha.GetProperty("VulnerabilityCount").GetInt32());
        Assert.Equal("Critical", forAlpha.GetProperty("Severity").GetString());
        Assert.Equal(["CVE-2099-0002 libb"], Listed(forAlpha));
        Assert.Equal(0, ProjectOf(only, beta.ProjectId).GetProperty("VulnerabilityCount").GetInt32());
        var shared = Assert.Single(only.GetProperty("Shared").EnumerateArray());
        Assert.Equal(new[] { alpha.ProjectId, beta.ProjectId }.Order(), shared.GetProperty("ProjectIds").EnumerateArray().Select(id => id.GetInt32()).Order());
        Assert.Equal(1, shared.GetProperty("VulnerabilityCount").GetInt32());
        Assert.Equal(Cve, shared.GetProperty("Vulnerabilities")[0].GetProperty("RuleId").GetString());
        Assert.Equal("2.0.0", shared.GetProperty("Vulnerabilities")[0].GetProperty("FixedVersion").GetString());
        Assert.All(await MatchesAsync(alpha.ProjectId), match => Assert.Equal(FirstPassAt, match.NotifiedAt));
        Assert.All(await MatchesAsync(beta.ProjectId), match => Assert.Equal(FirstPassAt, match.NotifiedAt));

        // The same pass again, and a pass long after: nothing is said twice.
        await PassAsync();
        AdvanceClock(TimeSpan.FromDays(2));
        await PassAsync();
        Assert.Single(Events());

        // Something new in one project: a new event, with only that project and only the new match.
        await ApplyAsync(Advisory("GHSA-3", "LibC", UpToFix, severity: "CRITICAL", cve: "CVE-2099-0003"));
        await AddItemAsync(alpha, "libc", "1.0.0");
        await PassAsync();
        var latest = Events().ToList();
        Assert.Equal(2, latest.Count);
        Assert.Equal(alpha.ProjectId, Assert.Single(latest[1].GetProperty("Projects").EnumerateArray()).GetProperty("ProjectId").GetInt32());
        Assert.Equal(["CVE-2099-0003 libc"], Listed(ProjectOf(latest[1], alpha.ProjectId)));
    }

    [Fact]
    public async Task OnlyAMatchWorthAMail_IsAnnounced_AMediumOneANoPublishedSeverityOneOrAToVerifyOneIsNot()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "medium", "1.0.0");
        await AddItemAsync(owner, "unpublished", "1.0.0");
        await AddItemAsync(owner, "toverify", "1.0.0");
        await ApplyAsync(
            Advisory("GHSA-1", "Medium", UpToFix, severity: "MODERATE", cve: "CVE-2099-0001"),
            Advisory("GHSA-2", "Unpublished", UpToFix, severity: null, cve: "CVE-2099-0002"),
            Advisory("GHSA-3", "ToVerify", Unreadable, cve: "CVE-2099-0003"));

        await PassAsync();

        Assert.Empty(Events());
        var matches = await MatchesAsync(owner.ProjectId);
        Assert.Equal(3, matches.Count);
        Assert.All(matches, match => Assert.Null(match.NotifiedAt));
        await using (var db = NewContext())
        {
            // Exploited in the wild: what the KEV feed would set. The advisory itself is unchanged.
            var advisory = await db.VulnerabilityAdvisories.SingleAsync(row => row.ExternalId == "GHSA-2", Ct);
            Assert.False(advisory.SeverityPublished);
            Assert.Equal(AnalysisSeverity.High, advisory.Severity);
            advisory.KevAddedAt = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync(Ct);
        }

        await PassAsync();

        // Exploited, and not even published: it does not wait for the night.
        var exploited = ProjectOf(Assert.Single(Events()), owner.ProjectId);
        Assert.Equal(["CVE-2099-0002 unpublished"], Listed(exploited));
        Assert.Equal(1, exploited.GetProperty("ExploitedCount").GetInt32());
        Assert.True(exploited.GetProperty("Vulnerabilities")[0].GetProperty("Exploited").GetBoolean());
    }

    [Fact]
    public async Task AFindingSomeoneDecidedOn_IsNotAnnouncedAgain_WhenThePackageMovesButStaysAffected()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var item = await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, severity: "CRITICAL"));
        await PassAsync();
        Assert.Single(Events());
        await using (var db = NewContext())
        {
            // The decision of a reviewer, as the governance sets it; not made through the decision service here.
            (await db.AnalysisFindings.SingleAsync(finding => finding.ProjectId == owner.ProjectId, Ct)).Status = AnalysisFindingStatus.Accepted;
            await db.SaveChangesAsync(Ct);
        }

        await MoveItemAsync(owner, item, "lib", "1.5.0");
        await PassAsync();

        Assert.Single(Events());
        var matches = await MatchesAsync(owner.ProjectId);
        var current = Assert.Single(matches, match => match.ClosedAt is null);
        Assert.Equal("1.5.0", current.Version);
        Assert.Null(current.NotifiedAt);
    }

    [Fact]
    public async Task AMatchThatComesBackAfterAFix_IsAnnouncedAgain()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        var item = await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, severity: "CRITICAL"));
        await PassAsync();
        Assert.Single(Events());

        var fixedItem = await MoveItemAsync(owner, item, "lib", "2.0.0");
        await PassAsync();
        Assert.Single(Events());
        Assert.NotNull(Assert.Single(await MatchesAsync(owner.ProjectId)).ClosedAt);

        AdvanceClock(TimeSpan.FromDays(1));
        await MoveItemAsync(owner, fixedItem, "lib", "1.0.0");
        await PassAsync();

        var events = Events().ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal([$"{Cve} lib"], Listed(ProjectOf(events[1], owner.ProjectId)));
        var match = Assert.Single(await MatchesAsync(owner.ProjectId));
        Assert.Null(match.ClosedAt);
        Assert.Equal(FirstPassAt.AddDays(1), match.NotifiedAt);
    }

    [Fact]
    public async Task ASendThatFails_LeavesTheMatchesToTheNextPass()
    {
        await ResetAndMigrateAsync();
        var alpha = await SeedProjectAsync("Alpha");
        var beta = await SeedProjectAsync("Beta");
        await AddItemAsync(alpha, "lib", "1.0.0");
        await AddItemAsync(beta, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, severity: "CRITICAL"));
        FailNextSend = new InvalidOperationException("the notification service is down");

        // The advisory half only: a whole pass raises the event twice, and the second raising would retry at once.
        await PassAsync(advisoriesOnly: true);

        // The one event failed and was given back: neither project is marked.
        Assert.Empty(Events());
        Assert.Null(Assert.Single(await MatchesAsync(alpha.ProjectId)).NotifiedAt);
        Assert.Null(Assert.Single(await MatchesAsync(beta.ProjectId)).NotifiedAt);

        await PassAsync(advisoriesOnly: true);

        Assert.Equal(2, Assert.Single(Events()).GetProperty("Projects").GetArrayLength());
        Assert.NotNull(Assert.Single(await MatchesAsync(alpha.ProjectId)).NotifiedAt);
        Assert.NotNull(Assert.Single(await MatchesAsync(beta.ProjectId)).NotifiedAt);
    }

    [Fact]
    public async Task TwoPassesClaimingTheSameMatches_AnnounceThemOnce()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix));
        await PassAsync();
        await using var first = NewContext();
        await using var second = NewContext();
        var ids = (await MatchesAsync(owner.ProjectId)).Select(match => match.Id).ToList();
        await using (var db = NewContext())
        {
            await db.VulnerabilityMatches.ExecuteUpdateAsync(update => update.SetProperty(match => match.NotifiedAt, (DateTime?)null), Ct);
        }

        var claims = await Task.WhenAll(
            new CveWatchAnnouncementRepository(first).ClaimAsync(ids, FirstPassAt.AddHours(1), Ct),
            new CveWatchAnnouncementRepository(second).ClaimAsync(ids, FirstPassAt.AddHours(1), Ct));

        Assert.Equal(1, claims.Count(claim => claim.Count > 0));
        Assert.Equal(ids, claims.Single(claim => claim.Count > 0));
    }
}
