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
/// Recette R-8, a drop on the planner's calendar of the blocks of an AI task and of a backup, driven through the page
/// like <see cref="PipelineSchedulePlanViewTests"/>: the real scheduler's own move callback is invoked as a drop does. It
/// proves what the page decides and sends (the endpoint and the body of each sort, a refusal that writes nothing, a drop
/// that changes nothing); it does not prove the browser's drag and drop, the backend's answer or the layout.
/// </summary>
public sealed class PipelineScheduleActionMoveTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Night = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan PlusOne = TimeSpan.FromHours(1);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test-plus-one", PlusOne, "Test", "Test");

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineScheduleActionMoveTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        clock.SetLocalTimeZone(Zone);
        Services.AddSingleton<TimeProvider>(clock);
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(4, "Aetheus", "aetheus", OrganizationRole.Owner) });
        // The server's plan answers for the window each request asks for, as the real endpoint's does.
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
                ServerActions = Actions()
            });
        });
    }

    private static ScheduledServerActionDto At(string id, ServerActionKind kind, int sourceId, string name, string cron, int hour, int minute, bool movable) => new()
    {
        Id = id,
        Kind = kind,
        SourceId = sourceId,
        SourceName = name,
        ProjectId = 12,
        ProjectName = "Aetheus",
        CronExpression = cron,
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = hour,
        Minute = minute,
        DurationMinutes = 30,
        IsPlanned = false,
        NextRunUtc = Night.AddHours(hour).AddMinutes(minute),
        Occurrences = [new() { StartUtc = Night.AddHours(hour).AddMinutes(minute), EndUtc = Night.AddHours(hour).AddMinutes(minute + 30) }],
        Movable = movable
    };

    /// <summary>An AI task at 02:00 UTC (03:00 for the reader), a backup at 01:15 UTC (02:15), an AI task the reader may
    /// not write at 05:00 UTC (06:00).</summary>
    private static List<ScheduledServerActionDto> Actions() =>
    [
        At("ai:3", ServerActionKind.AiTask, 3, "review", "0 0 2 * * *", 2, 0, movable: true),
        At("backup:5", ServerActionKind.AppBackup, 5, "orders", "15 1 * * *", 1, 15, movable: true),
        At("ai:9", ServerActionKind.AiTask, 9, "locked", "0 0 5 * * *", 5, 0, movable: false)
    ];

    private async Task<IRenderedComponent<PipelineSchedule>> RenderPageAsync()
    {
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync();
        var cut = Render<PipelineSchedule>();
        cut.WaitForState(() => cut.FindAll("[data-omni-scheduler-appointment]").Count > 0, BunitTestHelper.AsyncRenderWait);
        return cut;
    }

    private async Task DropAsync(IRenderedComponent<PipelineSchedule> cut, string actionId, DateTimeOffset newStart)
    {
        var block = cut.Find($"[data-omni-scheduler-appointment^='{actionId}|']");
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
    public async Task DroppingAnAiTask_WritesItsSixFieldScheduleInUtc_ThroughItsOwnEndpoint()
    {
        _handler.SetResponse("api/ai/tasks/3/schedule", HttpStatusCode.OK);
        var cut = await RenderPageAsync();

        // 03:00 local (02:00 UTC) dropped at 05:30 local, which is 04:30 UTC.
        await DropAsync(cut, "ai:3", Local(3, 5, 30));

        var write = Assert.Single(Writes());
        Assert.EndsWith("api/ai/tasks/3/schedule", write.Url, StringComparison.Ordinal);
        Assert.Contains("\"schedule\":\"0 30 4 * * *\"", write.Body, StringComparison.Ordinal);
        var toast = Assert.Single(Toasts(), notification => notification.Severity == OmniSeverity.Success);
        Assert.Contains("AI task review", toast.Message, StringComparison.Ordinal);
        Assert.Equal("Undo", toast.ActionText);
    }

    [Fact]
    public async Task DroppingABackup_WritesItsFiveFieldCronInUtc_ThroughItsOwnEndpoint()
    {
        _handler.SetResponse("api/backups/5/schedule", HttpStatusCode.OK);
        var cut = await RenderPageAsync();

        // 02:15 local (01:15 UTC) dropped at 03:45 local, which is 02:45 UTC.
        await DropAsync(cut, "backup:5", Local(3, 3, 45));

        var write = Assert.Single(Writes());
        Assert.EndsWith("api/backups/5/schedule", write.Url, StringComparison.Ordinal);
        Assert.Contains("\"scheduleCron\":\"45 2 * * *\"", write.Body, StringComparison.Ordinal);
        Assert.Contains("Backup orders", Assert.Single(Toasts(), notification => notification.Severity == OmniSeverity.Success).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActionTheReaderCannotWrite_IsRefusedInAToast_AndNothingIsSent()
    {
        var cut = await RenderPageAsync();

        await DropAsync(cut, "ai:9", Local(3, 7, 0));

        Assert.Empty(Writes());
        var toast = Assert.Single(Toasts());
        Assert.Equal("Move refused", toast.Title);
    }

    [Fact]
    public async Task ADropOnTheSameTime_WritesNothing()
    {
        var cut = await RenderPageAsync();

        await DropAsync(cut, "backup:5", Local(3, 2, 15));

        Assert.Empty(Writes());
    }

    [Fact]
    public async Task ARefusedWrite_IsSaidInAnErrorToast_NotAsASuccess()
    {
        _handler.SetResponse("api/ai/tasks/3/schedule", HttpStatusCode.Forbidden);
        var cut = await RenderPageAsync();

        await DropAsync(cut, "ai:3", Local(3, 5, 30));

        Assert.Single(Writes());
        Assert.DoesNotContain(Toasts(), notification => notification.Severity == OmniSeverity.Success);
    }
}
