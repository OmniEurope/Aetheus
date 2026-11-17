// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-8 (option B): a reboot Aetheus schedules, as a planner row. The card names it and its server, says
/// it cannot be moved, and the block info says the daily time with its zone and what Aetheus last did, in words.</summary>
public sealed class ServerRebootPlannerRowTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    public ServerRebootPlannerRowTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
    }

    private static ScheduledServerActionDto Reboot(ServerRebootScheduleDto schedule) => new()
    {
        Id = "reboot:7",
        Kind = ServerActionKind.ServerReboot,
        SourceId = 7,
        ServerId = 7,
        ServerName = "srv-prod-01",
        CronExpression = "5 2 * * *",
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = 1,
        Minute = 5,
        DurationMinutes = 5,
        IsPlanned = true,
        NextRunUtc = new DateTime(2026, 11, 8, 1, 5, 0, DateTimeKind.Utc),
        Movable = false,
        Reboot = schedule
    };

    private static ServerRebootScheduleDto Schedule() => new()
    {
        ServerId = 7,
        ServerName = "srv-prod-01",
        Enabled = true,
        Hour = 2,
        Minute = 5,
        TimeZoneId = "Europe/Paris",
        Version = 1,
        NextRunUtc = new DateTime(2026, 11, 8, 1, 5, 0, DateTimeKind.Utc)
    };

    private static AngleSharp.Dom.IElement Q<TComponent>(IRenderedComponent<TComponent> cut, string selector)
        where TComponent : Microsoft.AspNetCore.Components.IComponent =>
        cut.Find(selector);

    private IRenderedComponent<ServerActionBlockInfo> RenderInfo(ScheduledServerActionDto action) =>
        Render<ServerActionBlockInfo>(parameters => parameters.Add(info => info.Action, action));

    [Fact]
    public void TheCard_NamesTheRebootAndItsServer_AndIsReadOnly()
    {
        var action = Reboot(Schedule());
        var plan = new SchedulePlanDto { ServerActions = [action] };
        var cut = Render<ServerActionScheduleCard>(parameters => parameters
            .Add(card => card.Item, action)
            .Add(card => card.Summary, SchedulePlanSummary.Build(plan, TimeZoneInfo.Utc, Now))
            .Add(card => card.Zone, TimeZoneInfo.Utc)
            .Add(card => card.NowUtc, Now));

        var card = Q(cut, "[data-testid='schedule-server-card-reboot:7']");
        Assert.Contains("Reboot of srv-prod-01", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("Server action", Q(cut, "[data-testid='schedule-server-kind']").TextContent.Trim());
        Assert.NotNull(Q(cut, "[data-testid='schedule-read-only']"));
    }

    [Fact]
    public void TheInfo_SaysTheDailyTimeInItsZone_AndThePlannedReboot()
    {
        var cut = RenderInfo(Reboot(Schedule()));

        Assert.Equal("Every day at 02:05 (Europe/Paris)", Q(cut, "[data-testid='schedule-info-reboot-when']").TextContent.Trim());
        Assert.Equal("Planned: next reboot on 08/11/2026 01:05 UTC", Q(cut, "[data-testid='schedule-info-reboot-outcome']").TextContent.Trim());
        Assert.NotNull(Q(cut, "[data-testid='schedule-info-read-only']"));
    }

    [Fact]
    public void TheInfo_SaysAPostponementWithWhatRuns()
    {
        var cut = RenderInfo(Reboot(Schedule() with
        {
            LastOutcome = RebootScheduleOutcome.Postponed,
            LastOutcomeAtUtc = new DateTime(2026, 11, 7, 1, 5, 0, DateTimeKind.Utc),
            OutcomeDetail = "Deploy - web"
        }));

        Assert.Equal("Postponed since 07/11/2026 01:05 UTC: Deploy - web is running",
            Q(cut, "[data-testid='schedule-info-reboot-outcome']").TextContent.Trim());
    }

    [Fact]
    public void TheInfo_SaysASkipAndItsReason()
    {
        var cut = RenderInfo(Reboot(Schedule() with
        {
            LastOutcome = RebootScheduleOutcome.Skipped,
            SkipReason = RebootSkipReason.ServerBusy,
            LastOutcomeAtUtc = new DateTime(2026, 11, 7, 2, 6, 0, DateTimeKind.Utc),
            OutcomeDetail = "Build - nightly"
        }));

        Assert.Equal("Skipped on 07/11/2026 02:06 UTC: Build - nightly was still running after 60 minutes of waiting",
            Q(cut, "[data-testid='schedule-info-reboot-outcome']").TextContent.Trim());
    }

    [Fact]
    public void TheInfo_SaysADoneRebootAndARefusedOrderInWords()
    {
        var done = RenderInfo(Reboot(Schedule() with
        {
            LastOutcome = RebootScheduleOutcome.Ordered,
            LastOutcomeAtUtc = new DateTime(2026, 11, 7, 1, 5, 0, DateTimeKind.Utc),
            LastTaskStatus = TaskExecutionStatus.Success,
            LastTaskCompletedAtUtc = new DateTime(2026, 11, 7, 1, 6, 0, DateTimeKind.Utc)
        }));
        Assert.Equal("Done on 07/11/2026 01:06 UTC", Q(done, "[data-testid='schedule-info-reboot-outcome']").TextContent.Trim());

        var refused = RenderInfo(Reboot(Schedule() with
        {
            LastOutcome = RebootScheduleOutcome.Ordered,
            LastOutcomeAtUtc = new DateTime(2026, 11, 7, 1, 5, 0, DateTimeKind.Utc),
            LastTaskStatus = TaskExecutionStatus.Failed,
            LastTaskFailureReason = nameof(RebootOrderRefusal.ScheduleChanged)
        }));
        Assert.Equal("Refused by the agent on 07/11/2026 01:05 UTC: the schedule changed since the order",
            Q(refused, "[data-testid='schedule-info-reboot-outcome']").TextContent.Trim());
    }
}
