// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// A site administrator drags the CVE watch block of the planner's calendar to another time: the page reads the time of the drop
/// in the zone of the watch, sends it once to the administrator's endpoint as the new local time of the daily pass, and says
/// nothing is written for a block the plan did not give as movable or for a drop that changes nothing. Driven through the page like
/// <see cref="PipelineScheduleActionMoveTests"/>: the real scheduler's own move callback is invoked as a drop does. It proves what
/// the page decides and sends; it does not prove the browser's drag and drop or the backend's answer.
/// </summary>
public sealed class PipelineScheduleCveWatchMoveTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan PlusOne = TimeSpan.FromHours(1);

    // November: Paris is UTC+1, so a reader at UTC+1 and the watch read the same wall clock.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test-plus-one", PlusOne, "Test", "Test");

    private readonly BunitTestHelper.TestHandler _handler;
    private bool _movable = true;

    public PipelineScheduleCveWatchMoveTests()
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
                ServerActions = [CveWatch()]
            });
        });
    }

    /// <summary>The watch at 04:00 Paris, which is 03:00 UTC in November, drawn in the night of 2 to 3 November.</summary>
    private ScheduledServerActionDto CveWatch() => new()
    {
        Id = "cve:0",
        Kind = ServerActionKind.CveWatch,
        SourceId = 0,
        DailyTime = "04:00",
        DailyTimeZoneId = "Europe/Paris",
        DurationMinutes = 30,
        IsPlanned = false,
        NextRunUtc = new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc),
        Occurrences = [new() { StartUtc = new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 11, 3, 3, 30, 0, DateTimeKind.Utc) }],
        Movable = _movable
    };

    private async Task<IRenderedComponent<PipelineSchedule>> RenderPageAsync()
    {
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync();
        var cut = Render<PipelineSchedule>();
        cut.WaitForState(() => cut.FindAll("[data-omni-scheduler-appointment]").Count > 0, BunitTestHelper.AsyncRenderWait);
        return cut;
    }

    private static async Task DropAsync(IRenderedComponent<PipelineSchedule> cut, DateTimeOffset newStart)
    {
        var block = cut.Find("[data-omni-scheduler-appointment^='cve:0|']");
        var start = DateTimeOffset.Parse(block.GetAttribute("data-start")!, CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(block.GetAttribute("data-end")!, CultureInfo.InvariantCulture);
        var appointment = new OmniSchedulerAppointment(block.GetAttribute("data-omni-scheduler-appointment")!, "block", start, end);
        var move = new OmniSchedulerAppointmentMove(appointment, newStart, newStart + (end - start));
        await cut.InvokeAsync(() => cut.FindComponent<OmniScheduler>().Instance.OnAppointmentMove.InvokeAsync(move));
    }

    private static DateTimeOffset Local(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, PlusOne);

    private IReadOnlyList<(string Url, string? Body)> Writes() =>
        [.. _handler.RequestDetails.Where(request => request.Method == "PUT").Select(request => (request.Url, request.Body))];

    private IReadOnlyList<OmniNotificationMessage> Toasts() => [.. Services.GetRequiredService<OmniOverlayService>().Notifications];

    [Fact]
    public async Task TheBlockOfAnAdministrator_IsMovable_NotDrawnAsLocked()
    {
        var cut = await RenderPageAsync();

        var block = cut.Find("[data-omni-scheduler-appointment^='cve:0|']");
        Assert.DoesNotContain("schedule-block--locked", block.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DroppingTheCveWatch_WritesTheDroppedTimeAsTheNewDailyPass_ThroughTheAdministratorEndpoint()
    {
        _handler.SetResponse("api/vulnerability-watch/pass-time", HttpStatusCode.NoContent);
        var cut = await RenderPageAsync();

        await DropAsync(cut, Local(3, 5, 30));

        var write = Assert.Single(Writes());
        Assert.EndsWith("api/vulnerability-watch/pass-time", write.Url, StringComparison.Ordinal);
        Assert.Contains("\"passTime\":\"05:30\"", write.Body, StringComparison.Ordinal);
        var toast = Assert.Single(Toasts(), notification => notification.Severity == OmniSeverity.Success);
        Assert.Contains("05:30 (Europe/Paris)", toast.Message, StringComparison.Ordinal);
        Assert.Equal("Undo", toast.ActionText);
    }

    [Fact]
    public async Task ADropOnAnotherDayAtTheSameTime_WritesNothing_TheTimeBeingTheSameEveryDay()
    {
        var cut = await RenderPageAsync();

        await DropAsync(cut, Local(4, 4, 0));

        Assert.Empty(Writes());
    }

    [Fact]
    public async Task ABlockThePlanDidNotGiveAsMovable_IsRefusedInAToast_AndNothingIsSent()
    {
        _movable = false;
        var cut = await RenderPageAsync();

        await DropAsync(cut, Local(3, 7, 0));

        Assert.Empty(Writes());
        Assert.Equal("Move refused", Assert.Single(Toasts()).Title);
    }

    [Fact]
    public async Task ARefusedWrite_IsSaidInAnErrorToast_NotAsASuccess()
    {
        _handler.SetResponse("api/vulnerability-watch/pass-time", HttpStatusCode.Forbidden);
        var cut = await RenderPageAsync();

        await DropAsync(cut, Local(3, 5, 30));

        Assert.Single(Writes());
        Assert.DoesNotContain(Toasts(), notification => notification.Severity == OmniSeverity.Success);
    }
}
