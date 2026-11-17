// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// PLAN-005 lot 4, the night and week calendar of the build planner, driven through the page: the real
/// <see cref="OmniScheduler"/> is rendered and its own <c>OnAppointmentMove</c> callback is invoked with the move the
/// scheduler would report, as a drop does. HTTP is mocked, the resources are the real English ones, the clock stands
/// still (Monday 2026-11-02 19:00 for a reader one hour ahead of UTC, so the night in progress is 2 to 3 November).
/// This proves what the page decides, writes and shows for a drop; it does not prove the browser's drag and drop, the
/// real endpoints, the real layout or the veil (the plan's lot 5).
/// </summary>
public sealed class PipelineSchedulePlanViewTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Night = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan PlusOne = TimeSpan.FromHours(1);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test-plus-one", PlusOne, "Test", "Test");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSchedulePlanViewTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        clock.SetLocalTimeZone(Zone);
        Services.AddSingleton<TimeProvider>(clock);
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(4, "Aetheus", "aetheus", OrganizationRole.Owner) });
        SetPlan(Plan());
    }

    /// <summary>One task of every frequency, each with one run tonight, half an hour long, at a time of its own:
    /// 11 every evening 01:00, 12 Monday to Friday 02:00, 13 every two days 03:00, 14 weekly on Tuesday 04:00,
    /// 15 monthly on the 3rd 05:00 (all local), 16 custom, 17 switched off, 18 planned (never ran), 19 overlapping 11.</summary>
    private static List<ScheduledTaskDto> Plan() =>
    [
        Scheduled(11, "evening", ScheduleFrequency.EveryEvening, null, 0, 0),
        Scheduled(12, "workdays", ScheduleFrequency.Weekdays, null, 1, 0),
        Scheduled(13, "twodays", ScheduleFrequency.EveryTwoDays, null, 2, 0),
        Scheduled(14, "weekly", ScheduleFrequency.Weekly, 2, 3, 0),
        Scheduled(15, "monthly", ScheduleFrequency.Monthly, 3, 4, 0),
        new()
        {
            PipelineId = 16, PipelineName = "custom", Enabled = true, CronExpression = "*/20 5 * * *", Frequency = ScheduleFrequency.Custom,
            DurationMinutes = 30, IsPlanned = false, NextRunUtc = Night.AddHours(5),
            Occurrences = [new() { StartUtc = Night.AddHours(5), EndUtc = Night.AddHours(5).AddMinutes(30) }]
        },
        new()
        {
            PipelineId = 17, PipelineName = "paused", Enabled = false, CronExpression = "0 5 * * *", Frequency = ScheduleFrequency.EveryEvening,
            Hour = 5, Minute = 0, DurationMinutes = 30, IsPlanned = true
        },
        Scheduled(18, "unmeasured", ScheduleFrequency.EveryEvening, null, 6, 0) with { IsPlanned = true },
        Scheduled(19, "clash", ScheduleFrequency.EveryEvening, null, 0, 15) with
        {
            Occurrences = [new() { StartUtc = Night.AddMinutes(15), EndUtc = Night.AddMinutes(45), OverlapMinutes = 15 }]
        }
    ];

    private static ScheduledTaskDto Scheduled(int id, string name, ScheduleFrequency frequency, int? day, int hour, int minute)
    {
        var start = Night.AddHours(hour).AddMinutes(minute);
        var cron = $"{minute} {hour} * * *";
        return new ScheduledTaskDto
        {
            PipelineId = id,
            PipelineName = name,
            Enabled = true,
            CronExpression = cron,
            Frequency = frequency,
            Day = day,
            Hour = hour,
            Minute = minute,
            DurationMinutes = 30,
            IsPlanned = false,
            NextRunUtc = start,
            Occurrences = [new() { StartUtc = start, EndUtc = start.AddMinutes(30) }]
        };
    }

    private List<ScheduledTaskDto> _tasks = [];
    private int _capacity = 1;

    /// <summary>The server's plan: the tasks it holds, answered for the window each request asks for (its plan starts
    /// and ends where the request says, as the real endpoint's does), so that a page asking too short a window is
    /// seen as the calendar will see it.</summary>
    private void SetPlan(List<ScheduledTaskDto> tasks, int capacity = 1)
    {
        _tasks = tasks;
        _capacity = capacity;
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/schedule-plan?organizationId=4", _ =>
        {
            var (from, to) = LastRequestedWindow();
            return Task.FromResult(new SchedulePlanDto { OrganizationId = 4, FromUtc = from, ToUtc = to, ExecutionCapacity = _capacity, Tasks = _tasks });
        });
    }

    private (DateTime From, DateTime To) LastRequestedWindow()
    {
        var url = _handler.RequestsSnapshot().Last(request => request.Method == "GET" && request.Url.Contains("schedule-plan", StringComparison.Ordinal)).Url;
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        static DateTime Read(string? value) =>
            DateTime.Parse(value!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return (Read(query["from"]), Read(query["to"]));
    }

    private async Task<IRenderedComponent<PipelineSchedule>> RenderPageAsync()
    {
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync();
        var cut = Render<PipelineSchedule>();
        cut.WaitForState(() => cut.FindAll("[data-omni-scheduler-appointment]").Count > 0, BunitTestHelper.AsyncRenderWait);
        return cut;
    }

    private static IRenderedComponent<OmniScheduler> Scheduler(IRenderedComponent<PipelineSchedule> cut) => cut.FindComponent<OmniScheduler>();

    private static AngleSharp.Dom.IElement Block(IRenderedComponent<PipelineSchedule> cut, int pipelineId) =>
        cut.Find($"[data-omni-scheduler-appointment^='{pipelineId}|']");

    private static DateTimeOffset Local(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, PlusOne);

    private static string LocalTime(int hour, int minute) => new TimeOnly(hour, minute).ToString("t", CultureInfo.CurrentCulture);

    /// <summary>Drops the block of a pipeline at a new start, through the callback of the rendered scheduler: what its
    /// drag and drop (or its move button) reports, with the appointment the scheduler was given.</summary>
    private async Task DropAsync(IRenderedComponent<PipelineSchedule> cut, int pipelineId, DateTimeOffset newStart)
    {
        var block = Block(cut, pipelineId);
        var start = DateTimeOffset.Parse(block.GetAttribute("data-start")!, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(block.GetAttribute("data-end")!, CultureInfo.InvariantCulture);
        var appointment = new OmniSchedulerAppointment(block.GetAttribute("data-omni-scheduler-appointment")!, "block", start, end);
        var move = new OmniSchedulerAppointmentMove(appointment, newStart, newStart + (end - start));
        await cut.InvokeAsync(() => Scheduler(cut).Instance.OnAppointmentMove.InvokeAsync(move));
    }

    private IReadOnlyList<(string Url, string? Body)> Writes() =>
        [.. _handler.RequestDetails.Where(request => request.Method == "PUT").Select(request => (request.Url, request.Body))];

    private static UpdatePipelineScheduleRequest Sent(string? body) => JsonSerializer.Deserialize<UpdatePipelineScheduleRequest>(body!, WebJson)!;

    private IReadOnlyList<OmniNotificationMessage> Toasts() => [.. Services.GetRequiredService<OmniOverlayService>().Notifications];

    private int PlanReads() => _handler.RequestsSnapshot().Count(request => request.Url.Contains("schedule-plan", StringComparison.Ordinal));

    private void AnswerWrite(int pipelineId, Func<List<ScheduledTaskDto>, List<ScheduledTaskDto>>? afterwards = null) =>
        _handler.SetAsyncJsonResponse(HttpMethod.Put, $"api/pipelines/{pipelineId}/schedule", _ =>
        {
            if (afterwards is not null) SetPlan(afterwards(Plan()));
            return Task.FromResult(new PipelineDto { Id = pipelineId, Name = "p" });
        });

    // ---------- what the calendar draws ----------

    [Fact]
    public async Task TheNightIsTheDefaultView_ADayGridFrom2000To0800_InTheReadersZone_InQuarterHours()
    {
        var cut = await RenderPageAsync();

        var scheduler = Scheduler(cut).Instance;
        Assert.Equal(OmniCalendarView.Day, scheduler.View);
        Assert.Equal(new TimeOnly(20, 0), scheduler.DayStart);
        Assert.Equal(new TimeOnly(8, 0), scheduler.DayEnd);
        Assert.Equal(TimeSpan.FromMinutes(15), scheduler.SlotDuration);
        Assert.True(scheduler.MarkPastSlots);
        Assert.Same(Zone, scheduler.TimeZone);
        // The night in progress: its column is the 2nd of November, the evening the run of the 3rd at 01:00 belongs to.
        Assert.Equal(new DateOnly(2026, 11, 2), DateOnly.FromDateTime(scheduler.Date.DateTime));
        Assert.Equal(new[] { "Night", "Day", "Week", "Cards" }, cut.FindAll("#schedule-view-mode [role='radio']").Select(radio => radio.TextContent.Trim()).ToArray());
    }

    [Fact]
    public async Task TheOrientationBar_SwitchesTheSchedulerBetweenVerticalAndHorizontal_VerticalByDefault()
    {
        var cut = await RenderPageAsync();

        Assert.Equal(OmniOrientation.Vertical, Scheduler(cut).Instance.Orientation);
        Assert.Equal(new[] { "Vertical", "Horizontal" }, cut.FindAll("#schedule-orientation [role='radio']").Select(radio => radio.TextContent.Trim()).ToArray());

        cut.FindAll("#schedule-orientation [role='radio']").Single(radio => radio.TextContent.Trim() == "Horizontal").Click();
        Assert.Equal(OmniOrientation.Horizontal, Scheduler(cut).Instance.Orientation);

        cut.FindAll("#schedule-orientation [role='radio']").Single(radio => radio.TextContent.Trim() == "Vertical").Click();
        Assert.Equal(OmniOrientation.Vertical, Scheduler(cut).Instance.Orientation);
    }

    [Fact]
    public async Task EachOccurrenceOfASwitchedOnTaskIsABlock_ATaskSwitchedOffHasNone_AndTheViewAndTheCardSaySo()
    {
        var cut = await RenderPageAsync();

        var ids = cut.FindAll("[data-omni-scheduler-appointment]")
            .Select(block => block.GetAttribute("data-omni-scheduler-appointment")!.Split('|')[0]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "11", "12", "13", "14", "15", "16", "18", "19" }, ids);
        Assert.Equal(PipelineSchedulePlanView.AppointmentId(11, Night), Block(cut, 11).GetAttribute("data-omni-scheduler-appointment"));
        Assert.Equal("Switched-off pipelines are not drawn (1): they have no run.", cut.Find("[data-testid='schedule-view-off']").TextContent.Trim());
        Assert.Equal("Switched off: no run is planned", cut.Find("[data-testid='schedule-card-17'] [data-testid='schedule-next']").TextContent.Trim());
    }

    [Fact]
    public async Task ABlockSaysPlannedOverlapAndLockedInWords_AndByAPatternClass_NeverByColourAlone()
    {
        var cut = await RenderPageAsync();

        var planned = Block(cut, 18);
        Assert.Contains("unmeasured (planned)", planned.TextContent, StringComparison.Ordinal);
        Assert.Contains("schedule-block--planned", planned.ClassName, StringComparison.Ordinal);
        var overlap = Block(cut, 19);
        Assert.Contains("clash (overlap 15 min)", overlap.TextContent, StringComparison.Ordinal);
        Assert.Contains("schedule-block--overlap", overlap.ClassName, StringComparison.Ordinal);
        var locked = Block(cut, 16);
        Assert.Contains("custom (cannot be moved)", locked.TextContent, StringComparison.Ordinal);
        Assert.Contains("schedule-block--locked", locked.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule-block--", Block(cut, 11).ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain(" style=", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWeekIsTheOtherView_AWeekListOfOmniScheduler_ChosenInTheSelectBar()
    {
        var cut = await RenderPageAsync();

        cut.FindAll("#schedule-view-mode [role='radio']").Single(radio => radio.TextContent.Trim() == "Week").Click();

        cut.WaitForAssertion(() => Assert.Equal(OmniCalendarView.Week, Scheduler(cut).Instance.View), BunitTestHelper.AsyncRenderWait);
        Assert.Null(Scheduler(cut).Instance.DayStart);
        Assert.Equal(TimeSpan.FromMinutes(15), Scheduler(cut).Instance.SlotDuration);
    }

    /// <summary>R-6 and R-7: each of the three calendars (night, day, week) halves the slot height and writes the full
    /// date in the day header. The period title is off: the date is written once, in the grid, under the line of the controls.</summary>
    [Fact]
    public async Task EveryCalendar_IsCompact_WritesTheFullDayHeader_AndNoPeriodTitleBesideTheControls()
    {
        var cut = await RenderPageAsync();

        foreach (var mode in new[] { "Night", "Day", "Week" })
        {
            cut.FindAll("#schedule-view-mode [role='radio']").Single(radio => radio.TextContent.Trim() == mode).Click();
            cut.WaitForAssertion(() =>
            {
                var scheduler = Scheduler(cut).Instance;
                Assert.Equal(OmniSchedulerSlotHeight.Compact, scheduler.SlotHeight);
                Assert.Equal("dddd d MMMM yyyy", scheduler.DayHeaderFormat);
                Assert.False(scheduler.ShowPeriodTitle);
                // R-8: OE's own overlap mark (any shared minute) is off; the block says "overlap" only past the capacity.
                Assert.False(scheduler.ShowOverlaps);
            }, BunitTestHelper.AsyncRenderWait);
        }
        Assert.DoesNotContain("omni-scheduler__appointment--overlapping", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>The view and orientation bars are the first children of the calendar's wrapper, before the scheduler, so that the
    /// stylesheet floats them beside the scheduler's own header (previous, today, next): one line of controls, the date under it.
    /// It proves the markup order the CSS relies on; the layout itself is a browser matter.</summary>
    [Fact]
    public async Task TheViewAndOrientationBars_PrecedeTheScheduler_InOneWrapperWithIt_SoTheControlsShareOneLine()
    {
        var cut = await RenderPageAsync();

        var wrapper = cut.Find(".schedule-plan-calendar");
        var children = wrapper.Children.ToList();
        Assert.Equal("schedule-plan-bars", children[0].GetAttribute("data-testid"));
        Assert.NotNull(children[0].QuerySelector("#schedule-view-mode"));
        Assert.NotNull(children[0].QuerySelector("#schedule-orientation"));
        Assert.Contains("omni-scheduler", children[1].ClassName, StringComparison.Ordinal);
        Assert.NotNull(children[1].QuerySelector(".omni-scheduler__header"));
        Assert.Null(children[1].QuerySelector(".omni-scheduler__title"));
    }

    /// <summary>R-46: picking a view pushes it in the URL, and going back to the previous URL restores the previous view.</summary>
    [Fact]
    public async Task ThePickedView_IsPushedInTheUrl_AndTheBackNavigationRestoresTheView()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/pipelines/schedule");
        var cut = await RenderPageAsync();
        var before = nav.Uri;

        cut.FindAll("#schedule-view-mode [role='radio']").Single(radio => radio.TextContent.Trim() == "Week").Click();

        cut.WaitForAssertion(() => Assert.Equal(OmniCalendarView.Week, Scheduler(cut).Instance.View), BunitTestHelper.AsyncRenderWait);
        Assert.Contains("view=week", nav.Uri, StringComparison.Ordinal);

        nav.NavigateTo(before);

        cut.WaitForAssertion(() => Assert.Equal(OmniCalendarView.Day, Scheduler(cut).Instance.View), BunitTestHelper.AsyncRenderWait);
    }

    /// <summary>R-46: a URL carrying the view and the day opens on them; a malformed day or view is ignored.</summary>
    [Fact]
    public async Task TheUrlViewAndDay_OpenTheCalendarOnThem()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/schedule?view=week&date=2026-11-05");

        var cut = await RenderPageAsync();

        Assert.Equal(OmniCalendarView.Week, Scheduler(cut).Instance.View);
        Assert.Equal(new DateOnly(2026, 11, 5), DateOnly.FromDateTime(Scheduler(cut).Instance.Date.DateTime));
    }

    [Fact]
    public async Task AMalformedUrlViewAndDay_FallBackToTheNightInProgress()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/schedule?view=bogus&date=not-a-day");

        var cut = await RenderPageAsync();

        Assert.Equal(OmniCalendarView.Day, Scheduler(cut).Instance.View);
    }

    [Fact]
    public async Task UnderFortyRemTheCardsAreTheDefault_NoCalendarIsDrawn()
    {
        JSInterop.Setup<bool>("Aetheus.isNarrowViewport").SetResult(true);
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        var cut = Render<PipelineSchedule>();

        cut.WaitForState(() => cut.Markup.Contains("schedule-card-11", StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        Assert.Empty(cut.FindComponents<OmniScheduler>());
        Assert.Contains("schedule-view-mode", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCalendarHeadingExplainsTheEveryTwoDaysMonthEndGap_InItsHelpMark_NotUnderTheField()
    {
        var cut = await RenderPageAsync();

        Assert.Contains("the 31st and the 1st follow each other", cut.Find("[data-testid='schedule-plan-view']").InnerHtml, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid='schedule-plan-view'] .omni-heading__help-mark"));
    }

    [Fact]
    public async Task AnotherNightOutsideThePlansWindowReadsThePlanOfThatRange()
    {
        var cut = await RenderPageAsync();
        var before = PlanReads();

        // The scheduler's own "next period" button, seven times: the nights of the 3rd to the 8th lie inside the
        // window the page read (7 days from now), the night of the 9th does not, so its range is read from the API.
        for (var step = 0; step < 7; step++) cut.Find(".omni-scheduler__header > button:nth-of-type(3)").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request =>
            request.Url.Contains("schedule-plan", StringComparison.Ordinal) && request.Url.Contains("from=2026-11-08T23", StringComparison.Ordinal)),
            BunitTestHelper.AsyncRenderWait);
        Assert.True(PlanReads() > before);
    }

    // ---------- one drop, one write ----------

    [Fact]
    public async Task EveryEvening_OnlyTheTimeChanges_TheDateOfTheDropIsIgnored_AndTheToastSaysTheTimeIsCommon()
    {
        var cut = await RenderPageAsync();
        AnswerWrite(11);

        // Dropped on the next day's column at 01:30: the date is ignored, the time of day is kept.
        await DropAsync(cut, 11, Local(4, 1, 30));

        cut.WaitForAssertion(() => Assert.Single(Writes()), BunitTestHelper.AsyncRenderWait);
        var sent = Sent(Writes()[0].Body);
        Assert.Equal("api/pipelines/11/schedule", Writes()[0].Url.Split("http://test/")[1]);
        Assert.Equal((ScheduleFrequency.EveryEvening, (int?)null, 0, 30), (sent.Frequency!.Value, sent.Day, sent.Hour, sent.Minute));
        Assert.True(sent.Enabled);
        Assert.Equal("0 0 * * *", sent.ExpectedCronExpression);
        Assert.True(sent.ExpectedEnabled);
        var toast = Assert.Single(Toasts(), notification => notification.Severity == OmniSeverity.Success);
        Assert.Equal($"evening: Every evening at {LocalTime(1, 30)}. This time is common to all its runs.", toast.Message);
        Assert.Equal("Undo", toast.ActionText);
    }

    [Fact]
    public async Task Weekdays_EveryTwoDays_AndMonthly_ChangeOnlyTheirTime_EachWithOneWrite()
    {
        var cut = await RenderPageAsync();
        AnswerWrite(12);
        AnswerWrite(13);
        AnswerWrite(15);

        await DropAsync(cut, 12, Local(4, 3, 30));
        cut.WaitForAssertion(() => Assert.Single(Writes()), BunitTestHelper.AsyncRenderWait);
        await DropAsync(cut, 13, Local(3, 3, 15));
        cut.WaitForAssertion(() => Assert.Equal(2, Writes().Count), BunitTestHelper.AsyncRenderWait);
        await DropAsync(cut, 15, Local(3, 5, 30));
        cut.WaitForAssertion(() => Assert.Equal(3, Writes().Count), BunitTestHelper.AsyncRenderWait);

        var weekdays = Sent(Writes()[0].Body);
        Assert.Equal((ScheduleFrequency.Weekdays, (int?)null, 2, 30), (weekdays.Frequency!.Value, weekdays.Day, weekdays.Hour, weekdays.Minute));
        var twoDays = Sent(Writes()[1].Body);
        Assert.Equal((ScheduleFrequency.EveryTwoDays, (int?)null, 2, 15), (twoDays.Frequency!.Value, twoDays.Day, twoDays.Hour, twoDays.Minute));
        var monthly = Sent(Writes()[2].Body);
        Assert.Equal((ScheduleFrequency.Monthly, (int?)3, 4, 30), (monthly.Frequency!.Value, monthly.Day, monthly.Hour, monthly.Minute));
        Assert.Equal(3, Toasts().Count(notification => notification.Message.EndsWith("This time is common to all its runs.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Weekly_TheDayAndTheTimeChange_AndTheToastDoesNotClaimACommonTime()
    {
        var cut = await RenderPageAsync();
        AnswerWrite(14);

        // Tuesday 04:00 (03:00 UTC) dropped on Thursday the 5th at 04:30.
        await DropAsync(cut, 14, Local(5, 4, 30));

        cut.WaitForAssertion(() => Assert.Single(Writes()), BunitTestHelper.AsyncRenderWait);
        var sent = Sent(Writes()[0].Body);
        Assert.Equal((ScheduleFrequency.Weekly, (int?)4, 3, 30), (sent.Frequency!.Value, sent.Day, sent.Hour, sent.Minute));
        var toast = Assert.Single(Toasts(), notification => notification.Severity == OmniSeverity.Success);
        Assert.Equal($"weekly: Every {CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DayOfWeek.Thursday)} at {LocalTime(4, 30)}", toast.Message);
        Assert.DoesNotContain("common", toast.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterTheWrite_ThePlanIsReadAgain_AndTheBlockIsDrawnWhereTheServerNowSaysIt()
    {
        var cut = await RenderPageAsync();
        var before = PlanReads();
        AnswerWrite(11, tasks => [.. tasks.Select(task => task.PipelineId == 11
            ? task with { Hour = 0, Minute = 30, Occurrences = [new() { StartUtc = Night.AddMinutes(30), EndUtc = Night.AddMinutes(60) }] }
            : task)]);

        await DropAsync(cut, 11, Local(3, 1, 30));

        cut.WaitForAssertion(() => Assert.Contains("T01:30:00.0000000+01:00", Block(cut, 11).GetAttribute("data-start"), StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        Assert.True(PlanReads() > before);
        Assert.Single(Writes());
    }

    [Fact]
    public async Task ADropAtTheOriginalPosition_WritesNothing_AndSaysNothing()
    {
        var cut = await RenderPageAsync();

        await DropAsync(cut, 11, Local(3, 1, 0));
        await DropAsync(cut, 14, Local(3, 4, 0));

        Assert.Empty(Writes());
        Assert.Empty(Toasts());
    }

    [Fact]
    public async Task ADropOnAnotherDayAtTheSameTime_OfATaskWithOneCommonTime_WritesNothing_AndExplainsWhy()
    {
        var cut = await RenderPageAsync();

        await DropAsync(cut, 11, Local(4, 1, 0));

        Assert.Empty(Writes());
        var toast = Assert.Single(Toasts());
        Assert.Equal("evening has one time for all its runs: only the time of a drop counts, not the day.", toast.Message);
    }

    // ---------- refusals ----------

    [Fact]
    public async Task AMondayToFridayTimeThatFallsOnAnotherUtcDay_IsRefusedWithItsReason_AndNothingIsWritten()
    {
        var cut = await RenderPageAsync();

        // 00:30 local is 23:30 UTC the day before: "1-5" in UTC would run Tuesday to Saturday.
        await DropAsync(cut, 12, Local(3, 0, 30));

        Assert.Empty(Writes());
        var toast = Assert.Single(Toasts());
        Assert.Equal(OmniSeverity.Warning, toast.Severity);
        Assert.Equal("Move refused", toast.Title);
        Assert.Equal("At this time of day, Monday to Friday would fall on other days in UTC and run from Tuesday to Saturday. Choose a later time.", toast.Message);
    }

    [Fact]
    public async Task ACustomSchedule_CannotBeMoved_ItIsExplained_AndNothingIsWritten()
    {
        var cut = await RenderPageAsync();

        await DropAsync(cut, 16, Local(3, 5, 30));

        Assert.Empty(Writes());
        var toast = Assert.Single(Toasts());
        Assert.Equal("Move refused", toast.Title);
        Assert.StartsWith("This pipeline has a custom schedule", toast.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutWriteRight_TheBlockCannotBeMoved_ItIsExplained_AndNothingIsWritten()
    {
        Services.GetRequiredService<PermissionService>().SetPermissions(
            [new EffectivePermissionDto { ResourceType = ResourceType.Pipeline, ResourceId = null, Permission = Permission.Read }], isAdmin: false);
        var cut = await RenderPageAsync();

        await DropAsync(cut, 11, Local(3, 1, 30));

        Assert.Empty(Writes());
        var toast = Assert.Single(Toasts());
        Assert.Equal("You do not have the right to change this schedule.", toast.Message);
        Assert.Contains("evening (cannot be moved)", Block(cut, 11).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConflictAnsweredByTheServer_IsShownInTheAlertWithReload_NeverAsASuccess()
    {
        var cut = await RenderPageAsync();
        _handler.SetRawResponse(HttpMethod.Put, "api/pipelines/11/schedule", "{\"message\":\"stale\"}", "application/json", HttpStatusCode.Conflict);

        await DropAsync(cut, 11, Local(3, 1, 30));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-refusal']")), BunitTestHelper.AsyncRenderWait);
        Assert.Contains("The schedule changed", cut.Find("[data-testid='schedule-refusal']").TextContent, StringComparison.Ordinal);
        Assert.Contains("stale", cut.Find("[data-testid='schedule-refusal']").TextContent, StringComparison.Ordinal);
        Assert.Single(Writes());
        Assert.DoesNotContain(Toasts(), notification => notification.Severity == OmniSeverity.Success);
    }

    // ---------- R-38: the day view ----------

    [Fact]
    public async Task TheDayIsAFourthView_TheWholeDayInQuarterHours_AndItLivesInTheUrl()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/pipelines/schedule");
        var cut = await RenderPageAsync();

        cut.FindAll("#schedule-view-mode [role='radio']").Single(radio => radio.TextContent.Trim() == "Day").Click();

        cut.WaitForAssertion(() => Assert.Equal("schedule-day", Scheduler(cut).Instance.Id), BunitTestHelper.AsyncRenderWait);
        var scheduler = Scheduler(cut).Instance;
        Assert.Equal(OmniCalendarView.Day, scheduler.View);
        Assert.Equal(new TimeOnly(0, 0), scheduler.DayStart);
        Assert.Equal(new TimeOnly(23, 59), scheduler.DayEnd);
        Assert.Equal(TimeSpan.FromMinutes(15), scheduler.SlotDuration);
        Assert.Contains("view=day", nav.Uri, StringComparison.Ordinal);
        // 24 hours of quarter hours: 96 rows, the first at midnight (not 20:00 as the night's).
        Assert.Equal(96, cut.FindAll("#schedule-day tbody tr").Count);
        Assert.Equal("00:00", cut.Find("#schedule-day tbody tr time").GetAttribute("datetime"));
    }

    [Fact]
    public async Task ADayUrl_OpensTheDayView_OnTheDayOfTheUrl()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/schedule?view=day&date=2026-11-03");

        var cut = await RenderPageAsync();

        Assert.Equal("schedule-day", Scheduler(cut).Instance.Id);
        Assert.Equal(new DateOnly(2026, 11, 3), DateOnly.FromDateTime(Scheduler(cut).Instance.Date.DateTime));
    }

    // ---------- R-43: a drop costs one write and one read ----------

    [Fact]
    public async Task ThePageReadsTheWeekOfTheNightInProgress_SoTheCalendarDrawsFromThePlanItself()
    {
        var cut = await RenderPageAsync();

        // Monday 2026-11-02 19:00 local (UTC+1) on a night that began that evening: the window starts at the latest at
        // Monday's local midnight (Sunday's when weeks start on Sunday), a margin before it, and reaches 7 days ahead.
        var (from, to) = LastRequestedWindow();
        Assert.True(from <= new DateTime(2026, 11, 1, 23, 0, 0, DateTimeKind.Utc), $"The window started at {from:O}.");
        Assert.Equal(Now.AddDays(7), to);
        Assert.Null(Scheduler(cut).Instance.Load);
        Assert.Equal(8, Scheduler(cut).Instance.Items.Count);
    }

    [Fact]
    public async Task ADrop_IsOneWriteAndOnePlanRead_TheCalendarKeepsDrawingFromThePlan_NoSecondReadOfItsRange()
    {
        var cut = await RenderPageAsync();
        var before = PlanReads();
        AnswerWrite(11, tasks => [.. tasks.Select(task => task.PipelineId == 11
            ? task with { Hour = 0, Minute = 30, Occurrences = [new() { StartUtc = Night.AddMinutes(30), EndUtc = Night.AddMinutes(60) }] }
            : task)]);

        await DropAsync(cut, 11, Local(3, 1, 30));

        cut.WaitForAssertion(() => Assert.Contains("T01:30:00.0000000+01:00", Block(cut, 11).GetAttribute("data-start"), StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        Assert.Single(Writes());
        Assert.Equal(before + 1, PlanReads());
        Assert.Null(Scheduler(cut).Instance.Load);
    }

    [Fact]
    public async Task ANightOutsideThePlansWindow_IsLoadedOnDemand_AndTheNightsInsideIt_AreNot()
    {
        var cut = await RenderPageAsync();

        for (var step = 0; step < 7; step++) cut.Find(".omni-scheduler__header > button:nth-of-type(3)").Click();

        cut.WaitForAssertion(() => Assert.NotNull(Scheduler(cut).Instance.Load), BunitTestHelper.AsyncRenderWait);
    }

    // ---------- R-39: runs side by side within the capacity are not an overlap ----------

    // The server flags both runs of a pair that shares time: here "evening" (00:00 to 00:30 UTC) and "clash" (00:15 to 00:45).
    private static List<ScheduledTaskDto> Clashing() =>
        [.. Plan().Select(task => task.PipelineId == 11 ? task with { Occurrences = [task.Occurrences[0] with { OverlapMinutes = 15 }] } : task)];

    [Fact]
    public async Task WithRoomForTwoRuns_TwoRunsAtOnceAreNotAnOverlap_TheTileSaysTheCapacity()
    {
        SetPlan(Clashing(), capacity: 2);

        var cut = await RenderPageAsync();

        var tile = cut.Find("[data-testid='schedule-tile-overlaps']").TextContent;
        Assert.Contains("Never more than 2 runs at the same time", tile, StringComparison.Ordinal);
        Assert.DoesNotContain("evening and clash", tile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithRoomForOneRun_TwoRunsAtOnceAreAnOverlap()
    {
        SetPlan(Clashing());
        var cut = await RenderPageAsync();

        Assert.Contains("evening and clash, 15 min at the same time", cut.Find("[data-testid='schedule-tile-overlaps']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlockBeyondTheCapacitySaysSoInWords()
    {
        SetPlan(Plan(), capacity: 2);

        var cut = await RenderPageAsync();

        Assert.Contains("clash (over capacity 15 min)", Block(cut, 19).TextContent, StringComparison.Ordinal);
    }

    // ---------- R-44: the small info of a block ----------

    [Fact]
    public async Task AClickOnABlock_OpensTheInfoOfItsPipeline()
    {
        var cut = await RenderPageAsync();
        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

        Block(cut, 11).QuerySelector(".omni-scheduler__open")!.Click();

        Assert.Equal(1, dialog.OpenCount);
        Assert.Equal(typeof(PipelineScheduleBlockInfo), dialog.LastComponent);
        Assert.Equal("evening", dialog.LastTitle);
        Assert.Equal(11, ((ScheduledTaskDto)dialog.LastParameters!["Task"]!).PipelineId);
    }

    // ---------- undo ----------

    [Fact]
    public async Task Undo_WritesThePreviousTimeBack_AgainstWhatThePageNowReads()
    {
        var cut = await RenderPageAsync();
        AnswerWrite(11, tasks => [.. tasks.Select(task => task.PipelineId == 11
            ? task with { CronExpression = "30 0 * * *", Hour = 0, Minute = 30 }
            : task)]);
        await DropAsync(cut, 11, Local(3, 1, 30));
        cut.WaitForAssertion(() => Assert.Contains(Toasts(), notification => notification.ActionText == "Undo"), BunitTestHelper.AsyncRenderWait);
        var undo = Toasts().Single(notification => notification.ActionText == "Undo");

        await cut.InvokeAsync(() => undo.Action!());

        cut.WaitForAssertion(() => Assert.Equal(2, Writes().Count), BunitTestHelper.AsyncRenderWait);
        var back = Sent(Writes()[1].Body);
        Assert.Equal((ScheduleFrequency.EveryEvening, 0, 0), (back.Frequency!.Value, back.Hour, back.Minute));
        Assert.Equal("30 0 * * *", back.ExpectedCronExpression);
    }

    // ---------- R-26: the overlap tile shows the night, the page reads the plan once, the drag marks its target ----------

    /// <summary>R-26: choosing the overlap tile while the week (or the cards) is shown switches the planner to the night view, where
    /// the overlaps are drawn, and keeps the filter on the cards.</summary>
    [Fact]
    public async Task TheOverlapTile_SwitchesTheCalendarToTheNight_AndKeepsTheOverlapFilter()
    {
        // "evening" (11) and "clash" (19) share 15 minutes: the server marks both runs.
        SetPlan([.. Plan().Select(task => task.PipelineId == 11
            ? task with { Occurrences = [new() { StartUtc = Night, EndUtc = Night.AddMinutes(30), OverlapMinutes = 15 }] }
            : task)]);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/pipelines/schedule?view=week&date=2026-11-05");
        var cut = await RenderPageAsync();
        Assert.Equal(OmniCalendarView.Week, Scheduler(cut).Instance.View);

        cut.Find("[data-testid='schedule-tile-overlaps']").Click();

        cut.WaitForAssertion(() => Assert.Equal(OmniCalendarView.Day, Scheduler(cut).Instance.View), BunitTestHelper.AsyncRenderWait);
        Assert.Equal(new TimeOnly(20, 0), Scheduler(cut).Instance.DayStart);
        Assert.Contains("view=night", nav.Uri, StringComparison.Ordinal);
        // The day chosen in the URL is kept; the cards keep only the pipelines that share time.
        Assert.Contains("date=2026-11-05", nav.Uri, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("[data-testid='schedule-card-11']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-card-12']"));
    }

    /// <summary>R-26 (known limit "two reads at load"): the page reads the plan once when it opens, the calendar drawing that same plan.</summary>
    [Theory]
    [InlineData("/pipelines/schedule")]
    [InlineData("/pipelines/schedule?view=week")]
    public async Task ThePageReadsThePlanOnceAtLoad(string url)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        var cut = await RenderPageAsync();
        cut.WaitForState(() => cut.FindAll("[data-testid='schedule-card-11']").Count > 0, BunitTestHelper.AsyncRenderWait);

        Assert.Equal(1, PlanReads());
    }

    /// <summary>R-25 probe: the .NET half of a mouse drag as the page configures the scheduler (drag start on a block, drag enter on
    /// a cell): the scheduler marks the cell under the pointer as the drop target. A real browser drag is not exercised
    /// here (bUnit fires the events itself).</summary>
    [Fact]
    public async Task ADragOfABlockOverACell_MarksTheCellAsTheDropTarget()
    {
        var cut = await RenderPageAsync();
        AngleSharp.Dom.IElement TargetCell() => cut.FindAll("#schedule-night tr")
            .Single(row => row.QuerySelector("time[datetime='02:30']") is not null).QuerySelector("td")!;

        await cut.InvokeAsync(() => Block(cut, 11).DragStart());
        Assert.Contains("omni-scheduler--dragging", cut.Find("#schedule-night").ClassName, StringComparison.Ordinal);
        await cut.InvokeAsync(() => TargetCell().DragEnter());

        Assert.Contains("omni-scheduler__drop", TargetCell().ClassName, StringComparison.Ordinal);
    }
}
