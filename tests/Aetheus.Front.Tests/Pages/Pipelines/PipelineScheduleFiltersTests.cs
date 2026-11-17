// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R-8, the "Genre" and "Serveur" filters of the build planner and the read-only sources (the restore-check cadence
/// and the CVE watch), driven through the page like <see cref="PipelineScheduleActionMoveTests"/>: the real scheduler and
/// the real filter bar are rendered, HTTP answers the plan, the resources are the real English ones. It proves what the
/// page reads from the URL, keeps in it, draws and says; it does not prove the browser's rendering of the bar or the
/// backend's answer (the HTTP integration test proves who gets the CVE watch).
/// </summary>
public sealed class PipelineScheduleFiltersTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Night = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan PlusOne = TimeSpan.FromHours(1);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test-plus-one", PlusOne, "Test", "Test");

    private readonly BunitTestHelper.TestHandler _handler;
    private List<ScheduledServerActionDto> _actions = Actions();

    public PipelineScheduleFiltersTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        clock.SetLocalTimeZone(Zone);
        Services.AddSingleton<TimeProvider>(clock);
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(4, "Aetheus", "aetheus", OrganizationRole.Owner) });
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/schedule-plan?organizationId=4", _ =>
        {
            var url = _handler.RequestsSnapshot().Last(request => request.Method == "GET" && request.Url.Contains("schedule-plan", StringComparison.Ordinal)).Url;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            static DateTime Read(string? value) =>
                DateTime.Parse(value!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            return Task.FromResult(new SchedulePlanDto
            {
                OrganizationId = 4,
                FromUtc = Read(query["from"]),
                ToUtc = Read(query["to"]),
                Tasks = [Pipeline()],
                ServerActions = _actions
            });
        });
    }

    private static ScheduledTaskDto Pipeline() => new()
    {
        PipelineId = 11,
        PipelineName = "evening",
        Enabled = true,
        CronExpression = "0 0 0 * * *",
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = 0,
        Minute = 0,
        DurationMinutes = 30,
        IsPlanned = false,
        NextRunUtc = Night,
        Occurrences = [new() { StartUtc = Night, EndUtc = Night.AddMinutes(30) }]
    };

    private static ScheduledServerActionDto Source(
        string id, ServerActionKind kind, int sourceId, string name, int serverId, string serverName, int hour, string cron = "0 0 * * *") => new()
        {
            Id = id,
            Kind = kind,
            SourceId = sourceId,
            SourceName = name,
            ServerId = serverId,
            ServerName = serverName,
            ProjectId = kind == ServerActionKind.RkhunterScan ? null : 12,
            ProjectName = kind == ServerActionKind.RkhunterScan ? null : "Aetheus",
            CronExpression = cron,
            Frequency = ScheduleFrequency.EveryEvening,
            Hour = hour,
            Minute = 0,
            DurationMinutes = 20,
            IsPlanned = false,
            NextRunUtc = Night.AddHours(hour),
            Occurrences = [new() { StartUtc = Night.AddHours(hour), EndUtc = Night.AddHours(hour).AddMinutes(20) }],
            Movable = kind is ServerActionKind.RkhunterScan or ServerActionKind.AiTask or ServerActionKind.AppBackup
        };

    /// <summary>A scan of server 7, an AI task of a project (no server), a backup of server 8 and its restore check, and the
    /// CVE watch (one pass a day, read only).</summary>
    private static List<ScheduledServerActionDto> Actions() =>
    [
        Source("rkhunter:7", ServerActionKind.RkhunterScan, 7, string.Empty, 7, "srv-prod-01", 2),
        Source("ai:3", ServerActionKind.AiTask, 3, "review", 0, string.Empty, 3),
        Source("backup:5", ServerActionKind.AppBackup, 5, "orders", 8, "srv-db", 1),
        Source("restore:5", ServerActionKind.RestoreCheck, 5, "orders", 8, "srv-db", 4),
        Cve()
    ];

    private static ScheduledServerActionDto Cve(bool known = true) => new()
    {
        Id = "cve:0",
        Kind = ServerActionKind.CveWatch,
        DailyTime = "04:00",
        DailyTimeZoneId = "Europe/Paris",
        DurationMinutes = 5,
        IsPlanned = true,
        NextRunUtc = known ? Night.AddHours(1) : null,
        Occurrences = known
            ? [new() { StartUtc = Night.AddHours(1), EndUtc = Night.AddHours(1).AddMinutes(5) }, new() { StartUtc = Night.AddHours(2), EndUtc = Night.AddHours(2).AddMinutes(5) }]
            : [],
        Movable = false
    };

    private async Task<IRenderedComponent<PipelineSchedule>> RenderPageAsync(string query = "")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/schedule" + query);
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync();
        var cut = Render<PipelineSchedule>();
        cut.WaitForState(() => cut.FindAll("[data-testid^='schedule-card-'], [data-testid^='schedule-server-card-']").Count > 0
            || cut.Markup.Contains("Nothing matches", StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        return cut;
    }

    private static string[] CardIds(IRenderedComponent<PipelineSchedule> cut) =>
        [.. cut.FindAll("[data-testid^='schedule-card-'], [data-testid^='schedule-server-card-']")
            .Select(card => card.GetAttribute("data-testid")!.Replace("schedule-server-card-", string.Empty, StringComparison.Ordinal)
                .Replace("schedule-card-", "pipeline:", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    private static string[] BlockIds(IRenderedComponent<PipelineSchedule> cut) =>
        [.. cut.FindAll("[data-omni-scheduler-appointment]")
            .Select(block => block.GetAttribute("data-omni-scheduler-appointment")!.Split('|')[0])
            .Distinct().Order(StringComparer.Ordinal)];

    private static string[] Genres(IRenderedComponent<PipelineSchedule> cut) =>
        [.. cut.FindAll("#schedule-filter-kind [role='radio']").Select(radio => radio.TextContent.Trim())];

    private string Url => Services.GetRequiredService<NavigationManager>().Uri;

    // ---------- the filter bar ----------

    [Fact]
    public async Task TheGenreBar_OffersAllAndEachGenre_TheCveWatchIncluded_WhenThePlanHoldsIt()
    {
        var cut = await RenderPageAsync();

        Assert.Equal(new[] { "All", "Pipelines", "Server scans", "AI tasks", "Backups", "CVE watch" }, Genres(cut));
        Assert.Contains("All servers", cut.Find("#schedule-filter-server").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGenreBar_HasNoCveWatchEntry_WhenThePlanLacksIt_ASiteAdminOnlyHasOne()
    {
        _actions = [.. Actions().Where(action => action.Kind != ServerActionKind.CveWatch)];

        var cut = await RenderPageAsync();

        Assert.Equal(new[] { "All", "Pipelines", "Server scans", "AI tasks", "Backups" }, Genres(cut));
    }

    [Fact]
    public async Task TheServerList_NamesTheServersTheActionsRunOn_ByName_AndOnlyThose()
    {
        var cut = await RenderPageAsync();

        var options = cut.FindAll("#schedule-filter-server option").Select(option => option.TextContent.Trim()).ToArray();
        Assert.Equal(new[] { "All servers", "srv-db", "srv-prod-01" }, options);
    }

    // ---------- the URL ----------

    [Fact]
    public async Task WithoutAFilter_EverythingIsDrawn_ThePipelineTheScanTheAiTaskTheBackupTheRestoreCheckAndTheCveWatch()
    {
        var cut = await RenderPageAsync();

        Assert.Equal(new[] { "ai:3", "backup:5", "cve:0", "pipeline:11", "restore:5", "rkhunter:7" }, CardIds(cut));
        Assert.Equal(new[] { "11", "ai:3", "backup:5", "cve:0", "restore:5", "rkhunter:7" }, BlockIds(cut));
    }

    [Theory]
    [InlineData("pipeline", new[] { "pipeline:11" })]
    [InlineData("scan", new[] { "rkhunter:7" })]
    [InlineData("ai", new[] { "ai:3" })]
    [InlineData("backup", new[] { "backup:5", "restore:5" })]
    [InlineData("cve", new[] { "cve:0" })]
    public async Task TheKindInTheUrl_KeepsOnlyThatGenre_OnTheCards(string kind, string[] expected)
    {
        var cut = await RenderPageAsync($"?kind={kind}");

        Assert.Equal(expected, CardIds(cut));
    }

    [Fact]
    public async Task TheKindInTheUrl_KeepsOnlyThatGenre_OnTheCalendarToo()
    {
        var cut = await RenderPageAsync("?kind=cve");

        cut.WaitForAssertion(() => Assert.Equal(new[] { "cve:0" }, BlockIds(cut)), BunitTestHelper.AsyncRenderWait);
    }

    [Fact]
    public async Task TheServerInTheUrl_KeepsTheActionsOfThatServer_AndHidesThePipelinesAndTheActionsOfNoServer()
    {
        var cut = await RenderPageAsync("?server=8");

        Assert.Equal(new[] { "backup:5", "restore:5" }, CardIds(cut));
        cut.WaitForAssertion(() => Assert.Equal(new[] { "backup:5", "restore:5" }, BlockIds(cut)), BunitTestHelper.AsyncRenderWait);
    }

    [Fact]
    public async Task TheKindAndTheServerTogether_Intersect()
    {
        var cut = await RenderPageAsync("?kind=scan&server=8");

        Assert.Empty(CardIds(cut));
        Assert.Contains("Nothing matches these filters", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?kind=bogus")]
    [InlineData("?kind=PIPELINE")]
    [InlineData("?server=abc")]
    [InlineData("?server=-3")]
    [InlineData("?server=0")]
    [InlineData("?kind=&server=")]
    public async Task AnUnreadableFilterInTheUrl_MeansNotChosen_EverythingStaysDrawn(string query)
    {
        var cut = await RenderPageAsync(query);

        Assert.Equal(6, CardIds(cut).Length);
    }

    [Fact]
    public async Task PickingAGenre_PushesItInTheUrl_KeepingTheViewAndTheDay_AndRedrawsTheCards()
    {
        var cut = await RenderPageAsync("?view=week&date=2026-11-05");

        cut.FindAll("#schedule-filter-kind [role='radio']").Single(radio => radio.TextContent.Trim() == "Backups").Click();

        cut.WaitForAssertion(() => Assert.Equal(new[] { "backup:5", "restore:5" }, CardIds(cut)), BunitTestHelper.AsyncRenderWait);
        Assert.Contains("kind=backup", Url, StringComparison.Ordinal);
        Assert.Contains("view=week", Url, StringComparison.Ordinal);
        Assert.Contains("date=2026-11-05", Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickingAllGenres_RemovesTheKindFromTheUrl()
    {
        var cut = await RenderPageAsync("?kind=backup&server=8");

        cut.FindAll("#schedule-filter-kind [role='radio']").Single(radio => radio.TextContent.Trim() == "All").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("kind=", Url, StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        Assert.Contains("server=8", Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickingAServer_PushesItsIdInTheUrl_AndRedrawsTheCards()
    {
        var cut = await RenderPageAsync();

        // The options of the list are numbered by position: pick the one that names the server.
        var option = cut.FindAll("#schedule-filter-server option").Single(entry => entry.TextContent.Trim() == "srv-prod-01");
        cut.Find("#schedule-filter-server").Change(option.GetAttribute("value")!);

        cut.WaitForAssertion(() => Assert.Equal(new[] { "rkhunter:7" }, CardIds(cut)), BunitTestHelper.AsyncRenderWait);
        Assert.Contains("server=7", Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBackNavigation_RestoresThePreviousFilter()
    {
        var cut = await RenderPageAsync();
        var before = Url;
        cut.FindAll("#schedule-filter-kind [role='radio']").Single(radio => radio.TextContent.Trim() == "Pipelines").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "pipeline:11" }, CardIds(cut)), BunitTestHelper.AsyncRenderWait);

        Services.GetRequiredService<NavigationManager>().NavigateTo(before);

        cut.WaitForAssertion(() => Assert.Equal(6, CardIds(cut).Length), BunitTestHelper.AsyncRenderWait);
    }

    [Fact]
    public async Task TheTiles_StayThoseOfTheWholePlan_WhateverTheFilter_AndTheCveWatchIsNoRunOfTheOrganization()
    {
        var all = await RenderPageAsync();
        var unfiltered = all.Find("[data-testid='schedule-tile-tasks']").TextContent;
        var filtered = (await RenderPageAsync("?kind=cve")).Find("[data-testid='schedule-tile-tasks']").TextContent;

        Assert.Equal(unfiltered, filtered);
        // The pipeline, the scan, the AI task, the backup and the restore check: five runs, never the CVE watch.
        Assert.StartsWith("5", all.Find("[data-testid='schedule-tile-tasks']").TextContent.Trim(), StringComparison.Ordinal);
    }

    // ---------- the read-only sources ----------

    [Fact]
    public async Task TheCveWatchCard_SaysItsDailyTimeThatItIsNotCountedAndThatItIsReadOnly_WithNoCronAndNoOverlapClaim()
    {
        var cut = await RenderPageAsync("?kind=cve");

        var card = cut.Find("[data-testid='schedule-server-card-cve:0']");
        Assert.Contains("CVE watch", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("CVE watch", card.QuerySelector("[data-testid='schedule-server-kind']")!.TextContent.Trim());
        Assert.Equal("Every day at 04:00 (Europe/Paris)", card.QuerySelector("[data-testid='schedule-frequency']")!.TextContent.Trim());
        Assert.Empty(card.QuerySelectorAll("[data-testid='schedule-cron']"));
        Assert.NotNull(card.QuerySelector("[data-testid='schedule-not-counted']"));
        Assert.Equal("Read only", card.QuerySelector("[data-testid='schedule-read-only']")!.TextContent.Trim());
        Assert.Empty(card.QuerySelectorAll("[data-testid='schedule-no-overlap']"));
        Assert.Empty(card.QuerySelectorAll("[data-testid='schedule-overlap']"));
        Assert.Equal("/admin/cve-sources", card.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task TheCveWatchWithNoOccurrenceInTheWindow_SaysNoRunIsComing_NoBlockIsDrawn()
    {
        _actions = [Cve(known: false)];

        var cut = await RenderPageAsync();

        Assert.Equal("No run in the coming days", cut.Find("[data-testid='schedule-server-card-cve:0'] [data-testid='schedule-next']").TextContent.Trim());
        Assert.DoesNotContain("cve:0", BlockIds(cut));
    }

    [Fact]
    public async Task TheCveWatchBlock_IsMarkedPlannedAndLocked_InWords()
    {
        var cut = await RenderPageAsync("?kind=cve");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-omni-scheduler-appointment]")), BunitTestHelper.AsyncRenderWait);
        var block = cut.Find("[data-omni-scheduler-appointment^='cve:0|']");
        Assert.Contains("CVE watch (planned, cannot be moved)", block.OuterHtml, StringComparison.Ordinal);
        Assert.Contains("schedule-block--locked", block.OuterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DroppingTheCveWatchBlockThePlanGaveAsReadOnly_WritesNothing_AndSaysOnlyASiteAdministratorMovesIt()
    {
        var cut = await RenderPageAsync("?kind=cve");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-omni-scheduler-appointment]")), BunitTestHelper.AsyncRenderWait);
        var block = cut.Find("[data-omni-scheduler-appointment^='cve:0|']");
        var start = DateTimeOffset.Parse(block.GetAttribute("data-start")!, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(block.GetAttribute("data-end")!, CultureInfo.InvariantCulture);
        var appointment = new OmniSchedulerAppointment(block.GetAttribute("data-omni-scheduler-appointment")!, "block", start, end);
        var newStart = start.AddHours(2);

        await cut.InvokeAsync(() => cut.FindComponent<OmniScheduler>().Instance.OnAppointmentMove
            .InvokeAsync(new OmniSchedulerAppointmentMove(appointment, newStart, newStart + (end - start))));

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "PUT");
        var toast = Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications);
        Assert.Equal("Only a site administrator can move the CVE watch.", toast.Message);
    }

    [Fact]
    public async Task TheRestoreCheckCard_IsItsOwnKind_ReadOnly_AndOpensTheBackups()
    {
        var cut = await RenderPageAsync("?kind=backup");

        var card = cut.Find("[data-testid='schedule-server-card-restore:5']");
        Assert.Contains("Restore check orders", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("Restore check", card.QuerySelector("[data-testid='schedule-server-kind']")!.TextContent.Trim());
        Assert.Equal("srv-db", card.QuerySelector("[data-testid='schedule-server-name']")!.TextContent.Trim());
        Assert.Equal("/backups", card.QuerySelector("a")!.GetAttribute("href"));
        var readOnly = card.QuerySelector("[data-testid='schedule-read-only']")!;
        Assert.Equal("The restore-check cadence is changed in the backup policy, not in the planner.", readOnly.GetAttribute("title"));
    }

    [Fact]
    public async Task DroppingARestoreCheckBlock_WritesNothing_AndSaysItIsChangedInThePolicy()
    {
        var cut = await RenderPageAsync("?kind=backup");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-omni-scheduler-appointment^='restore:5|']")), BunitTestHelper.AsyncRenderWait);
        var block = cut.Find("[data-omni-scheduler-appointment^='restore:5|']");
        var start = DateTimeOffset.Parse(block.GetAttribute("data-start")!, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(block.GetAttribute("data-end")!, CultureInfo.InvariantCulture);
        var appointment = new OmniSchedulerAppointment(block.GetAttribute("data-omni-scheduler-appointment")!, "block", start, end);
        var newStart = start.AddHours(1);

        await cut.InvokeAsync(() => cut.FindComponent<OmniScheduler>().Instance.OnAppointmentMove
            .InvokeAsync(new OmniSchedulerAppointmentMove(appointment, newStart, newStart + (end - start))));

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "PUT");
        var toast = Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications);
        Assert.Equal("The restore-check cadence is changed in the backup policy, not in the planner.", toast.Message);
    }
}
