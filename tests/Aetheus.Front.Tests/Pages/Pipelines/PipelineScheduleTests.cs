// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// PLAN-005 lot 3, the build planner page, driven through its markup against a mocked HTTP API with the real
/// English resources (so a sentence is asserted as the reader reads it) and a clock that stands still, in a zone
/// one hour ahead of UTC. The mocked HTTP proves what the page sends and shows for a given answer; it does not
/// prove the real endpoints, the real layout or the real veil (see the plan's lot 3 note).
/// </summary>
public sealed class PipelineScheduleTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tomorrow = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly BunitTestHelper.TestHandler _handler;
    // Read when a test runs, never in the constructor: asking the container closes it to new registrations.
    private ImmediateDialogService _dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    public PipelineScheduleTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test-plus-one", TimeSpan.FromHours(1), "Test", "Test"));
        Services.AddSingleton<TimeProvider>(clock);
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(4, "Aetheus", "aetheus", OrganizationRole.Owner), new(9, "Other", "other", OrganizationRole.Member) });
        SetPlan(4, Plan());
    }

    /// <summary>"nightly" and "docs" share half an hour tomorrow night; "weekly" is alone; "paused" is switched off.</summary>
    private static List<ScheduledTaskDto> Plan() =>
    [
        new()
        {
            PipelineId = 1, PipelineName = "nightly", Enabled = true, CronExpression = "0 0 * * *", Frequency = ScheduleFrequency.EveryEvening,
            Hour = 0, Minute = 0, DurationMinutes = 72, IsPlanned = false, NextRunUtc = Tomorrow,
            Occurrences = [new() { StartUtc = Tomorrow, EndUtc = Tomorrow.AddMinutes(72), OverlapMinutes = 42 }]
        },
        new()
        {
            PipelineId = 2, PipelineName = "docs", Enabled = true, CronExpression = "30 0 * * 1-5", Frequency = ScheduleFrequency.Weekdays,
            Hour = 0, Minute = 30, DurationMinutes = 60, IsPlanned = true, NextRunUtc = Tomorrow.AddMinutes(30),
            Occurrences = [new() { StartUtc = Tomorrow.AddMinutes(30), EndUtc = Tomorrow.AddMinutes(90), OverlapMinutes = 42 }]
        },
        new()
        {
            PipelineId = 3, PipelineName = "weekly", Enabled = true, CronExpression = "0 3 * * 1", Frequency = ScheduleFrequency.Weekly,
            Day = 1, Hour = 3, Minute = 0, DurationMinutes = 30, IsPlanned = true, NextRunUtc = new DateTime(2026, 11, 9, 3, 0, 0, DateTimeKind.Utc),
            Occurrences = [new() { StartUtc = new DateTime(2026, 11, 9, 3, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 11, 9, 3, 30, 0, DateTimeKind.Utc) }]
        },
        new()
        {
            PipelineId = 4, PipelineName = "paused", Enabled = false, CronExpression = "0 5 * * *", Frequency = ScheduleFrequency.EveryEvening,
            Hour = 5, Minute = 0, DurationMinutes = 30, IsPlanned = true
        },
        new()
        {
            PipelineId = 5, PipelineName = "odd", Enabled = true, CronExpression = "*/20 6 * * *", Frequency = ScheduleFrequency.Custom,
            DurationMinutes = 30, IsPlanned = true, NextRunUtc = Tomorrow.AddHours(6)
        }
    ];

    private void SetPlan(int organizationId, List<ScheduledTaskDto> tasks) =>
        _handler.SetJsonResponse(HttpMethod.Get, $"api/pipelines/schedule-plan?organizationId={organizationId}", new SchedulePlanDto
        {
            OrganizationId = organizationId,
            FromUtc = Now,
            ToUtc = Now.AddDays(7),
            Tasks = tasks
        });

    private async Task<IRenderedComponent<PipelineSchedule>> RenderPageAsync(string waitFor = "schedule-card-1")
    {
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync();
        var cut = Render<PipelineSchedule>();
        cut.WaitForState(() => cut.Markup.Contains(waitFor, StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        return cut;
    }

    private static string Text(IRenderedComponent<PipelineSchedule> cut, string testId, int pipelineId) =>
        cut.Find($"[data-testid='schedule-card-{pipelineId}'] [data-testid='{testId}']").TextContent.Trim();

    private static string LocalTime(int hour, int minute) =>
        new TimeOnly(hour, minute).ToString("t", CultureInfo.CurrentCulture);

    // The request as the server reads it, so an assertion is on a typed value and not on a JSON path.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static UpdatePipelineScheduleRequest Sent(string? body) =>
        JsonSerializer.Deserialize<UpdatePipelineScheduleRequest>(body!, WebJson)!;

    private IReadOnlyList<(HttpMethod Method, string Url, string? Body)> Writes() =>
        [.. _handler.RequestDetails.Where(request => request.Method == "PUT")
            .Select(request => (new HttpMethod(request.Method), request.Url, request.Body))];

    // ---------- what a card says ----------

    [Fact]
    public async Task ACardSaysItsFrequencyInWordsAtTheReadersClock_WithTheUtcTimeBesideIt()
    {
        var cut = await RenderPageAsync();

        // 00:00 UTC is 01:00 for a reader one hour ahead; the engine's 00:00 UTC stays visible, secondary.
        Assert.Equal($"Every evening at {LocalTime(1, 0)}", Text(cut, "schedule-frequency", 1));
        Assert.Equal($"(i.e. {LocalTime(0, 0)} UTC)", Text(cut, "schedule-utc", 1));
        Assert.Equal($"Monday to Friday at {LocalTime(1, 30)}", Text(cut, "schedule-frequency", 2));
        Assert.Equal($"Every {CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DayOfWeek.Monday)} at {LocalTime(4, 0)}", Text(cut, "schedule-frequency", 3));
        Assert.DoesNotContain("EveryEvening", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("0 0 * * *", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomScheduleIsShownAsWritten_BecauseItHasNoWords()
    {
        var cut = await RenderPageAsync();

        Assert.Equal("Custom schedule", Text(cut, "schedule-frequency", 5));
        Assert.Equal("*/20 6 * * *", Text(cut, "schedule-cron", 5));
    }

    [Fact]
    public async Task TheDurationSaysWhetherItIsPlannedOrTheAverageOfRealRuns()
    {
        var cut = await RenderPageAsync();

        Assert.Equal("Average duration: 1 h 12", Text(cut, "schedule-duration", 1));
        Assert.Equal("Planned duration: 1 h", Text(cut, "schedule-duration", 2));
        Assert.Equal("Planned duration: 30 min", Text(cut, "schedule-duration", 3));
    }

    [Fact]
    public async Task TheNextRunIsGivenAtTheReadersClock()
    {
        var cut = await RenderPageAsync();

        var expected = TimeZoneInfo.ConvertTimeFromUtc(Tomorrow, TimeZoneInfo.CreateCustomTimeZone("test-plus-one", TimeSpan.FromHours(1), "Test", "Test"))
            .ToString("g", CultureInfo.CurrentCulture);
        Assert.Equal($"Next: {expected}", Text(cut, "schedule-next", 1));
    }

    [Fact]
    public async Task AnOverlapIsAnIconAndAText_NamingTheOtherPipeline_NeverAColourAlone()
    {
        var cut = await RenderPageAsync();

        var badge = cut.Find("[data-testid='schedule-card-1'] [data-testid='schedule-overlap']");
        Assert.Equal("At the same time as docs", badge.TextContent.Trim());
        Assert.NotEmpty(badge.QuerySelectorAll("svg, .omni-icon, [class*='omni-icon']"));
        Assert.Equal("At the same time as nightly", Text(cut, "schedule-overlap", 2));
        Assert.Equal("No overlap over 7 days", Text(cut, "schedule-no-overlap", 3));
        Assert.Empty(cut.FindAll("[data-testid='schedule-card-3'] [data-testid='schedule-overlap']"));
    }

    [Fact]
    public async Task ASwitchedOffScheduleSaysSoWithAnIcon_AndHasNoOverlapVerdict()
    {
        var cut = await RenderPageAsync();

        var badge = cut.Find("[data-testid='schedule-card-4'] [data-testid='schedule-off']");
        Assert.Equal("Switched off", badge.TextContent.Trim());
        Assert.NotEmpty(badge.QuerySelectorAll("svg, .omni-icon, [class*='omni-icon']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-card-4'] [data-testid='schedule-no-overlap']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-card-4'] [data-testid='schedule-overlap']"));
    }

    // ---------- tiles ----------

    [Fact]
    public async Task TheTilesCountTheSchedulesAndTheOverlaps()
    {
        var cut = await RenderPageAsync();

        Assert.Contains("4", cut.Find("[data-testid='schedule-tile-tasks']").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 switched off", cut.Find("[data-testid='schedule-tile-tasks']").TextContent, StringComparison.Ordinal);
        Assert.Contains("nightly and docs, 42 min at the same time", cut.Find("[data-testid='schedule-tile-overlaps']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingTheOverlapTileKeepsOnlyThePipelinesThatShareTime_AndSelectingAgainShowsThemAll()
    {
        var cut = await RenderPageAsync();

        cut.Find("[data-testid='schedule-tile-overlaps']").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='schedule-card-3']")));
        Assert.NotEmpty(cut.FindAll("[data-testid='schedule-card-1']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='schedule-card-2']"));

        cut.Find("[data-testid='schedule-tile-overlaps']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-card-3']")));
    }

    // ---------- the page's shape ----------

    [Fact]
    public async Task ThePageHasOneMainActionVisible_AndTheRestInMenus_AndNoInlineStyle()
    {
        var cut = await RenderPageAsync();

        Assert.Single(cut.FindAll("[data-testid='schedule-main-action']"));
        Assert.Equal("Schedule", cut.Find("[data-testid='schedule-main-action']").TextContent.Trim());
        // The page's one main action is the only call-to-action button; Refresh and the pipelines list are menu entries.
        Assert.Single(cut.FindAll("button.omni-button--primary"));
        Assert.Empty(cut.FindAll("button.omni-button--secondary, button.omni-button--danger"));
        Assert.NotNull(cut.FindMenuItem("Refresh"));
        Assert.DoesNotContain(" style=", cut.Markup, StringComparison.Ordinal);
        // A card shows no button of its own: edit, switch and open are entries of its menu.
        Assert.Empty(cut.FindAll("[data-testid='schedule-card-1'] button:not(.omni-overflow-menu__trigger)"));
    }

    [Fact]
    public async Task TheTimeZoneIsExplainedInTheHelpMark_NotInATextUnderTheHeading()
    {
        var cut = await RenderPageAsync();

        Assert.Contains("Times are in your time zone (Test)", cut.Markup, StringComparison.Ordinal);
        // One mark on the calendar heading (lot 4), one on the schedules heading, which carries the time zone.
        Assert.Equal(2, cut.FindAll(".omni-heading__help-mark").Count);
        Assert.Single(cut.FindAll("[data-testid='schedule-plan-view'] .omni-heading__help-mark"));
    }

    [Fact]
    public async Task WithoutAnActiveOrganization_ThePageSaysSo_AndReadsNoPlan()
    {
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto>());
        var cut = Render<PipelineSchedule>();

        cut.WaitForAssertion(() => Assert.Contains("Choose an organization", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Url.Contains("schedule-plan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchingOrganization_ReadsTheOtherOrganizationsPlan()
    {
        SetPlan(9, [new() { PipelineId = 77, PipelineName = "elsewhere", Enabled = true, Frequency = ScheduleFrequency.EveryEvening, Hour = 1, Minute = 0, NextRunUtc = Tomorrow }]);
        var cut = await RenderPageAsync();

        await cut.InvokeAsync(() => Services.GetRequiredService<ActiveOrganizationService>().SetActiveAsync(9));

        cut.WaitForState(() => cut.Markup.Contains("schedule-card-77", StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        Assert.DoesNotContain("schedule-card-1\"", cut.Markup, StringComparison.Ordinal);
    }

    // ---------- rights ----------

    [Fact]
    public async Task WithoutWriteRights_TheActionsAreDisabledAndSayWhy_ButTheCardsAreReadable()
    {
        var permissions = Services.GetRequiredService<PermissionService>();
        permissions.SetPermissions(
            [new EffectivePermissionDto { ResourceType = ResourceType.Pipeline, ResourceId = null, Permission = Permission.Read }], isAdmin: false);
        var cut = await RenderPageAsync();

        var main = cut.Find("[data-testid='schedule-main-action']");
        Assert.True(main.HasAttribute("disabled"));
        Assert.Equal("You do not have the right to change this schedule.", main.GetAttribute("title"));
        Assert.Equal("Read only", Text(cut, "schedule-read-only", 1));
        var edit = cut.FindMenuItem("Change frequency")!;
        Assert.True(edit.HasAttribute("disabled") || edit.GetAttribute("aria-disabled") == "true");
        Assert.Equal("You do not have the right to change this schedule.", edit.GetAttribute("title"));
        Assert.Contains("Every evening at", Text(cut, "schedule-frequency", 1), StringComparison.Ordinal);
    }

    // ---------- dialogs ----------

    [Fact]
    public async Task TheMainActionOpensTheDialogToSchedule_WithTheOrganizationAndThePipelinesAlreadyScheduled()
    {
        var cut = await RenderPageAsync();

        cut.Find("[data-testid='schedule-main-action']").Click();

        Assert.Equal(typeof(PipelineScheduleDialog), _dialog.LastComponent);
        Assert.Null(_dialog.LastParameters!["Item"]);
        Assert.Equal(4, _dialog.LastParameters["OrganizationId"]);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ((IReadOnlyCollection<int>)_dialog.LastParameters["ScheduledPipelineIds"]!).Order());
    }

    [Fact]
    public async Task EditingACardOpensTheDialogOnThatTask()
    {
        var cut = await RenderPageAsync();

        cut.FindMenuItem("Change frequency")!.Click();

        Assert.Equal(typeof(PipelineScheduleDialog), _dialog.LastComponent);
        var item = Assert.IsType<ScheduledTaskDto>(_dialog.LastParameters!["Item"]);
        Assert.Equal(1, item.PipelineId);
    }

    [Fact]
    public async Task ASavedDialogMakesThePageReadThePlanAgain_AndItsUndoPutsThePreviousValueBack()
    {
        var cut = await RenderPageAsync();
        var undo = new UpdatePipelineScheduleRequest { Frequency = ScheduleFrequency.Weekdays, Hour = 0, Minute = 30, Enabled = true };
        var saved = new PipelineScheduleDialogResult(true, false, 2, "docs", "Monday to Friday at 02:00", Undo: undo);
        _dialog.OpenResult = saved;
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/2/schedule", new PipelineDto { Id = 2, Name = "docs" });
        var plansBefore = _handler.RequestsSnapshot().Count(request => request.Url.Contains("schedule-plan", StringComparison.Ordinal));

        cut.FindMenuItem("Change frequency")!.Click();

        cut.WaitForAssertion(() => Assert.True(
            _handler.RequestsSnapshot().Count(request => request.Url.Contains("schedule-plan", StringComparison.Ordinal)) > plansBefore));
        // The dialog says the schedule was saved (its own toast); the page hands it the way to undo.
        var onUndo = Assert.IsType<Func<PipelineScheduleDialogResult, Task>>(_dialog.LastParameters!["OnUndo"]);
        await cut.InvokeAsync(() => onUndo(saved));

        cut.WaitForAssertion(() => Assert.Single(Writes()), BunitTestHelper.AsyncRenderWait);
        Assert.Equal("api/pipelines/2/schedule", Writes()[0].Url.Split("http://test/")[1]);
        var sent = Sent(Writes()[0].Body);
        Assert.Equal(ScheduleFrequency.Weekdays, sent.Frequency);
        Assert.Equal(30, sent.Minute);
        // What the undo expects is what the page reads: docs is scheduled "30 0 * * 1-5" and enabled.
        Assert.Equal("30 0 * * 1-5", sent.ExpectedCronExpression);
        Assert.True(sent.ExpectedEnabled);
    }

    [Fact]
    public async Task AnUndoFromTheToastRedrawsThePage_WithThePlanItReadBack()
    {
        var cut = await RenderPageAsync();
        var undo = new UpdatePipelineScheduleRequest { Frequency = ScheduleFrequency.Weekdays, Hour = 0, Minute = 30, Enabled = true };
        var saved = new PipelineScheduleDialogResult(true, false, 2, "docs", "Monday to Friday at 02:00", Undo: undo);
        _dialog.OpenResult = saved;
        _handler.SetAsyncJsonResponse(HttpMethod.Put, "api/pipelines/2/schedule", _ =>
        {
            SetPlan(4, [.. Plan().Select(task => task.PipelineId == 2 ? task with { Enabled = false } : task)]);
            return Task.FromResult(new PipelineDto { Id = 2, Name = "docs" });
        });
        cut.FindMenuItem("Change frequency")!.Click();
        var onUndo = Assert.IsType<Func<PipelineScheduleDialogResult, Task>>(_dialog.LastParameters!["OnUndo"]);

        // A toast's button is not an event of the page: nothing redraws it unless the write does.
        await cut.InvokeAsync(() => onUndo(saved));

        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-card-2'] [data-testid='schedule-off']")),
            BunitTestHelper.AsyncRenderWait);
    }

    // ---------- writes: switch off, refusals, undo ----------

    [Fact]
    public async Task SwitchingOff_SendsOnlyTheSwitchWithWhatWasRead_AndOffersUndo_WhichPutsTheValueBack()
    {
        var cut = await RenderPageAsync();
        // The write is answered and, from then on, the plan says the pipeline is switched off: what Undo must expect.
        _handler.SetAsyncJsonResponse(HttpMethod.Put, "api/pipelines/1/schedule", _ =>
        {
            SetPlan(4, [.. Plan().Select(task => task.PipelineId == 1 ? task with { Enabled = false } : task)]);
            return Task.FromResult(new PipelineDto { Id = 1, Name = "nightly" });
        });

        cut.FindMenuItem("Switch off")!.Click();

        cut.WaitForAssertion(() => Assert.Single(Writes()), BunitTestHelper.AsyncRenderWait);
        var body = Sent(Writes()[0].Body);
        Assert.Equal("api/pipelines/1/schedule", Writes()[0].Url.Split("http://test/")[1]);
        Assert.False(body.Enabled);
        Assert.Null(body.Frequency);
        Assert.Equal("0 0 * * *", body.ExpectedCronExpression);
        Assert.True(body.ExpectedEnabled);

        var overlay = Services.GetRequiredService<OmniOverlayService>();
        cut.WaitForAssertion(() => Assert.Contains(overlay.Notifications, toast => toast.ActionText == "Undo"), BunitTestHelper.AsyncRenderWait);
        var toast = overlay.Notifications.Single(notification => notification.ActionText == "Undo");
        Assert.Equal("nightly is switched off.", toast.Message);

        await cut.InvokeAsync(() => toast.Action!());

        cut.WaitForAssertion(() => Assert.Equal(2, Writes().Count), BunitTestHelper.AsyncRenderWait);
        var undoBody = Sent(Writes()[1].Body);
        Assert.True(undoBody.Enabled);
        Assert.Null(undoBody.Frequency);
        Assert.False(undoBody.ExpectedEnabled);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "The schedule changed")]
    [InlineData(HttpStatusCode.Forbidden, "Not allowed")]
    [InlineData(HttpStatusCode.NotFound, "Pipeline not found")]
    public async Task ARefusedWrite_IsSaidInAnAlertWithReload_AndNeverSilentlyOrAsASuccess(HttpStatusCode status, string title)
    {
        var cut = await RenderPageAsync();
        _handler.SetRawResponse(HttpMethod.Put, "api/pipelines/1/schedule", "{\"message\":\"the reason\"}", "application/json", status);

        cut.FindMenuItem("Switch off")!.Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-refusal']")), BunitTestHelper.AsyncRenderWait);
        var alert = cut.Find("[data-testid='schedule-refusal']");
        Assert.Contains(title, alert.TextContent, StringComparison.Ordinal);
        Assert.Contains("the reason", alert.TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(alert.QuerySelectorAll("[data-testid='schedule-refusal-reload']"));
        Assert.DoesNotContain(Services.GetRequiredService<OmniOverlayService>().Notifications, toast => toast.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task Reload_AfterAConflict_ReadsThePlanAgain_AndClearsTheAlert()
    {
        var cut = await RenderPageAsync();
        _handler.SetRawResponse(HttpMethod.Put, "api/pipelines/1/schedule", "{\"message\":\"stale\"}", "application/json", HttpStatusCode.Conflict);
        cut.FindMenuItem("Switch off")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-refusal']")), BunitTestHelper.AsyncRenderWait);
        var plansBefore = _handler.RequestsSnapshot().Count(request => request.Url.Contains("schedule-plan", StringComparison.Ordinal));

        cut.Find("[data-testid='schedule-refusal-reload']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='schedule-refusal']")), BunitTestHelper.AsyncRenderWait);
        Assert.True(_handler.RequestsSnapshot().Count(request => request.Url.Contains("schedule-plan", StringComparison.Ordinal)) > plansBefore);
    }

    [Fact]
    public async Task APlanThatCannotBeRead_IsSaidInTheAlert()
    {
        _handler.SetRawResponse(HttpMethod.Get, "api/pipelines/schedule-plan?organizationId=4", "{\"message\":\"cannot read\"}", "application/json", HttpStatusCode.Forbidden);

        var cut = await RenderPageAsync("schedule-refusal");

        Assert.Contains("cannot read", cut.Find("[data-testid='schedule-refusal']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInactiveOwnerIsWarnedAfterASwitch_NotSilently()
    {
        var cut = await RenderPageAsync();
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/1/schedule",
            new PipelineDto { Id = 1, Name = "nightly", OwnerUsername = "gone", OwnerInactive = true });

        cut.FindMenuItem("Switch off")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(Services.GetRequiredService<OmniOverlayService>().Notifications,
            toast => toast.Severity == OmniSeverity.Warning), BunitTestHelper.AsyncRenderWait);
    }
}
