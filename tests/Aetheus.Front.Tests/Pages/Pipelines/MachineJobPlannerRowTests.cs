// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-27 (option A): a job the machine schedules by itself, as a planner row. The card and the block info say
/// it is the machine's own job, say what schedules it and when, and say it cannot be moved nor counted in the overlaps.</summary>
public sealed class MachineJobPlannerRowTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    public MachineJobPlannerRowTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
    }

    private static ScheduledServerActionDto DailyTimer() => new()
    {
        Id = "machine-job:7:0",
        Kind = ServerActionKind.MachineJob,
        SourceId = 896,
        SourceName = "logrotate.timer",
        ServerId = 7,
        ServerName = "srv-prod-01",
        CronExpression = "0 0 * * *",
        Frequency = ScheduleFrequency.Custom,
        DailyTime = "00:00",
        DailyTimeZoneId = "Europe/Paris",
        DurationMinutes = 5,
        IsPlanned = true,
        NextRunUtc = new DateTime(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc),
        Movable = false,
        MachineJob = new MachineJobEntryDto
        {
            Source = MachineJobSource.SystemdTimer,
            Name = "logrotate.timer",
            CronExpression = "0 0 * * *",
            TimeZoneId = "Europe/Paris",
            Detail = "logrotate.timer"
        }
    };

    private static ScheduledServerActionDto EveryFiveMinutes() => DailyTimer() with
    {
        Id = "machine-job:7:1",
        SourceId = 897,
        SourceName = "php",
        CronExpression = "*/5 * * * *",
        DailyTime = null,
        DailyTimeZoneId = null,
        MachineJob = new MachineJobEntryDto
        {
            Source = MachineJobSource.CronDirectory,
            Name = "php",
            User = "www-data",
            CronExpression = "*/5 * * * *",
            TimeZoneId = "Europe/Paris",
            Detail = "/etc/cron.d/webapp"
        }
    };

    private static ScheduledServerActionDto PeriodicByAnacron() => DailyTimer() with
    {
        Id = "machine-job:7:2",
        SourceId = 898,
        SourceName = "/etc/cron.daily",
        CronExpression = "25 6 * * *",
        DailyTime = "06:25",
        MachineJob = new MachineJobEntryDto
        {
            Source = MachineJobSource.PeriodicDirectory,
            Name = "/etc/cron.daily",
            User = "root",
            CronExpression = "25 6 * * *",
            TimeZoneId = "Europe/Paris",
            Conditional = true,
            Detail = "/etc/crontab"
        }
    };

    private static ScheduledServerActionDto NextInstantOnly() => DailyTimer() with
    {
        Id = "machine-job:7:3",
        SourceId = 899,
        SourceName = "apt-daily.timer",
        CronExpression = string.Empty,
        DailyTime = null,
        DailyTimeZoneId = null,
        MachineJob = new MachineJobEntryDto
        {
            Source = MachineJobSource.SystemdTimer,
            Name = "apt-daily.timer",
            AtUtc = new DateTime(2026, 11, 12, 18, 14, 0, DateTimeKind.Utc),
            Detail = "apt-daily.timer"
        }
    };

    private IRenderedComponent<ServerActionScheduleCard> RenderCard(ScheduledServerActionDto action)
    {
        var plan = new SchedulePlanDto { ServerActions = [action] };
        return Render<ServerActionScheduleCard>(parameters => parameters
            .Add(card => card.Item, action)
            .Add(card => card.Summary, SchedulePlanSummary.Build(plan, TimeZoneInfo.Utc, Now))
            .Add(card => card.Zone, TimeZoneInfo.Utc)
            .Add(card => card.NowUtc, Now));
    }

    private IRenderedComponent<ServerActionBlockInfo> RenderInfo(ScheduledServerActionDto action) =>
        Render<ServerActionBlockInfo>(parameters => parameters.Add(info => info.Action, action));

    [Fact]
    public void TheCard_NamesTheJobAndItsServer_SaysWhenAndWhatSchedulesIt_AndThatItIsReadOnlyAndNotCounted()
    {
        var cut = RenderCard(DailyTimer());

        var card = cut.Find("[data-testid='schedule-server-card-machine-job:7:0']");
        Assert.Contains("Machine job logrotate.timer", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("Machine job", cut.Find("[data-testid='schedule-server-kind']").TextContent.Trim());
        Assert.Equal("srv-prod-01", cut.Find("[data-testid='schedule-server-name']").TextContent.Trim());
        Assert.Equal("Every day at 00:00 (Europe/Paris)", cut.Find("[data-testid='schedule-frequency']").TextContent.Trim());
        Assert.Equal("systemd timer", cut.Find("[data-testid='schedule-machine-job-source']").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-testid='schedule-read-only']"));
        Assert.NotNull(cut.Find("[data-testid='schedule-not-counted']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-overlap']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-no-overlap']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-cron']"));
    }

    [Fact]
    public void TheCard_ForANonDailyCronJob_SaysItsScheduleZoneAndUser_NotAGuessedDailyTime()
    {
        var cut = RenderCard(EveryFiveMinutes());

        Assert.Equal("On the schedule */5 * * * * (Europe/Paris)", cut.Find("[data-testid='schedule-frequency']").TextContent.Trim());
        Assert.Equal("cron.d file, runs as www-data", cut.Find("[data-testid='schedule-machine-job-source']").TextContent.Trim());
    }

    [Fact]
    public void TheCard_ForARunPartsDirectoryThatAnacronMayRun_SaysTheTimeIsTheEarliestOne()
    {
        var cut = RenderCard(PeriodicByAnacron());

        Assert.Equal("Every day at 06:25 (Europe/Paris)", cut.Find("[data-testid='schedule-frequency']").TextContent.Trim());
        Assert.Equal(
            "Periodic scripts (run-parts), runs as root (earliest time: anacron may run it later)",
            cut.Find("[data-testid='schedule-machine-job-source']").TextContent.Trim());
    }

    [Fact]
    public void TheCard_ForATimerKnownByItsNextInstantOnly_SaysSo()
    {
        var cut = RenderCard(NextInstantOnly());

        Assert.Equal("Next run only, no repeating schedule known", cut.Find("[data-testid='schedule-frequency']").TextContent.Trim());
    }

    [Fact]
    public void TheInfo_SaysWhatRunsWhenWhatSchedulesItAndTheFile_AndThatItCannotBeMoved()
    {
        var cut = RenderInfo(EveryFiveMinutes());

        Assert.Equal("php", cut.Find("[data-testid='schedule-info-machine-job-name']").TextContent.Trim());
        Assert.Equal("On the schedule */5 * * * * (Europe/Paris)", cut.Find("[data-testid='schedule-info-machine-job-when']").TextContent.Trim());
        Assert.Equal("cron.d file, runs as www-data", cut.Find("[data-testid='schedule-info-machine-job-source']").TextContent.Trim());
        Assert.Equal("/etc/cron.d/webapp", cut.Find("[data-testid='schedule-info-machine-job-detail']").TextContent.Trim());
        Assert.Equal("Scheduled by the machine itself and read by its agent: Aetheus does not run or change this job.",
            cut.Find("[data-testid='schedule-info-source']").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-testid='schedule-info-read-only']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-info-movable']"));
    }

    [Fact]
    public void TheWords_AreInFrench_WithTheSameMeaning()
    {
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.French());
        var cut = RenderCard(PeriodicByAnacron());

        Assert.Contains("Tâche de la machine /etc/cron.daily", cut.Find("[data-testid='schedule-server-card-machine-job:7:2']").TextContent, StringComparison.Ordinal);
        Assert.Equal("Tâche de la machine", cut.Find("[data-testid='schedule-server-kind']").TextContent.Trim());
        Assert.Equal(
            "Scripts périodiques (run-parts), exécutée par root (heure au plus tôt : anacron peut la lancer plus tard)",
            cut.Find("[data-testid='schedule-machine-job-source']").TextContent.Trim());
    }

    [Fact]
    public void TheMachineJobsGenre_IsOfferedOnlyWhenAJobIsPresent_AndFiltersOnIt()
    {
        var plan = new SchedulePlanDto { ServerActions = [DailyTimer()] };

        Assert.Equal(ScheduleGenres.MachineJob, ScheduleGenres.Of(ServerActionKind.MachineJob));
        Assert.Contains(ScheduleGenres.MachineJob, PipelineScheduleFilter.Genres(plan, chosen: null));
        Assert.Single(PipelineScheduleFilter.Apply(plan, ScheduleGenres.MachineJob, serverId: null).ServerActions);
        Assert.Empty(PipelineScheduleFilter.Apply(plan, ScheduleGenres.Reboot, serverId: null).ServerActions);
        Assert.DoesNotContain(ScheduleGenres.MachineJob, PipelineScheduleFilter.Genres(new SchedulePlanDto(), chosen: null));
        Assert.Equal(ScheduleGenres.MachineJob, PipelineScheduleFilter.ParseKind("machine"));
    }

    [Fact]
    public void TwoJobsOfOneServer_GetTwoDistinctPlannerRows_NeitherMeetingAMachineReboot()
    {
        var reboot = DailyTimer() with { Kind = ServerActionKind.MachineReboot };

        Assert.NotEqual(ServerActionPlannerTask.RowId(DailyTimer()), ServerActionPlannerTask.RowId(EveryFiveMinutes()));
        Assert.NotEqual(ServerActionPlannerTask.RowId(DailyTimer()), ServerActionPlannerTask.RowId(reboot));
    }
}
