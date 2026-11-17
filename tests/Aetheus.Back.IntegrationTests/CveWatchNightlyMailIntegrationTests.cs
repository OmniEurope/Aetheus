// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data;
using Aetheus.Shared.Components.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// The daily CVE mail of the vulnerability watch on PostgreSQL (recette R-25 and R-26, decisions of 2026-11-03): once a day,
/// five minutes after the daily recap, ONE event for all the projects listing only what no mail has told yet; nothing new,
/// no mail (what stays open is the recap's job). Real matching, publication, digest, schedule and settings rows; the
/// clock is moved by hand; only the delivery of the event is recorded instead of made, so the recipients, the SMTP
/// delivery and the inbox are not proved here (the narrowing per recipient is proved in <c>UserNotificationDigestTests</c>).
/// The clock starts at 09:00 in Paris (2026-11-01 08:00 UTC, winter time).
/// </summary>
public sealed class CveWatchNightlyMailIntegrationTests(PostgresFixture fixture) : CveWatchTestBase(fixture)
{
    private List<JsonElement> Events() =>
        [.. Sent.Where(sent => sent.EventType == NotificationEventTypes.CveDetected).Select(sent => JsonDocument.Parse(sent.Json).RootElement.Clone())];

    private static IEnumerable<JsonElement> Vulnerabilities(JsonElement payload) =>
        payload.GetProperty("Projects").EnumerateArray().SelectMany(project => project.GetProperty("Vulnerabilities").EnumerateArray());

    private static string[] NewOnes(JsonElement payload) =>
        [.. Vulnerabilities(payload).Where(item => item.GetProperty("IsNew").GetBoolean()).Select(item => item.GetProperty("PackageName").GetString()!)];

    [Fact]
    public async Task ThePublishedHigh_IsToldOnlyFiveMinutesAfterTheRecap_OnceADay_ThenNeverMailedAgainWhileNothingIsNew()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, severity: "HIGH"));
        await SetRecapHourAsync("10:00");
        await PassAsync();

        // 09:00 in Paris: the High did not leave with the pass, and 10:05 (five minutes after the recap) has not come.
        Assert.Empty(Events());
        Assert.Equal(DailyRecapScheduleOutcome.NotYet, await NightAsync());
        Assert.Empty(Events());

        // 10:00, the recap's own hour: the CVE mail waits its five minutes.
        AdvanceClock(TimeSpan.FromHours(1));
        Assert.Equal(DailyRecapScheduleOutcome.NotYet, await NightAsync());
        Assert.Empty(Events());

        AdvanceClock(TimeSpan.FromMinutes(5));
        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());

        var first = Assert.Single(Events());
        Assert.True(first.GetProperty("Digest").GetBoolean());
        Assert.Equal(["lib"], NewOnes(first));
        Assert.NotNull(Assert.Single(await MatchesAsync(owner.ProjectId)).NotifiedAt);

        // The same day, however often the scheduler looks: nothing more.
        Assert.Equal(DailyRecapScheduleOutcome.AlreadySent, await NightAsync());
        Assert.Single(Events());

        // Next day: still open but nothing new, so no CVE mail at all (the daily recap says it is still open).
        AdvanceClock(TimeSpan.FromDays(1));
        await PassAsync();
        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());
        Assert.Single(Events());
    }

    [Fact]
    public async Task TheDailyMail_ListsOnlyWhatIsNew_NotWhatWasAlreadyTold()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "old", "1.0.0");
        await AddItemAsync(owner, "fresh", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Old", UpToFix, severity: "HIGH"));
        await SetRecapHourAsync("08:00");
        await PassAsync();
        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());
        Assert.Equal(["old"], NewOnes(Assert.Single(Events())));

        AdvanceClock(TimeSpan.FromDays(1));
        await ApplyAsync(Advisory("GHSA-2", "Fresh", UpToFix, severity: "HIGH"));
        await PassAsync();
        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());

        // Only the new High; the one told yesterday stays in the daily recap.
        var second = Events()[1];
        Assert.Equal(["fresh"], [.. Vulnerabilities(second).Select(item => item.GetProperty("PackageName").GetString()!)]);
    }

    [Fact]
    public async Task TheNightlyMail_IsOneMailForAllTheProjects_NotOnePerProject()
    {
        await ResetAndMigrateAsync();
        var alpha = await SeedProjectAsync("Alpha");
        var beta = await SeedProjectAsync("Beta");
        var gamma = await SeedProjectAsync("Gamma");
        foreach (var project in new[] { alpha, beta, gamma })
            await AddItemAsync(project, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, severity: "HIGH"));
        await SetRecapHourAsync("08:00");
        await PassAsync();

        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());

        // Three projects, one event.
        var night = Assert.Single(Events());
        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            night.GetProperty("Projects").EnumerateArray().Select(project => project.GetProperty("ProjectName").GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ANightThatFailsToSend_GivesTheNewMatchesBack_SoTheNextNightTellsThemAsNew()
    {
        await ResetAndMigrateAsync();
        var owner = await SeedProjectAsync("Alpha");
        await AddItemAsync(owner, "lib", "1.0.0");
        await ApplyAsync(Advisory("GHSA-1", "Lib", UpToFix, severity: "HIGH"));
        await SetRecapHourAsync("08:00");
        await PassAsync();
        FailNextSend = new InvalidOperationException("the notification service is down");

        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());

        Assert.Empty(Events());
        Assert.Null(Assert.Single(await MatchesAsync(owner.ProjectId)).NotifiedAt);

        AdvanceClock(TimeSpan.FromDays(1));
        Assert.Equal(DailyRecapScheduleOutcome.Due, await NightAsync());
        Assert.Equal(["lib"], NewOnes(Assert.Single(Events())));
    }
}
