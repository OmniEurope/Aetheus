// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-11 (option A): a reboot the machine decides by itself, as a planner row. The card and the block info say
/// it is managed by the machine, say what decides it and when, and say it cannot be moved nor counted in the overlaps.</summary>
public sealed class MachineRebootPlannerRowTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    public MachineRebootPlannerRowTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
    }

    private static ScheduledServerActionDto DailyUnattended() => new()
    {
        Id = "machine-reboot:7:0",
        Kind = ServerActionKind.MachineReboot,
        SourceId = 224,
        ServerId = 7,
        ServerName = "srv-prod-01",
        CronExpression = "0 2 * * *",
        Frequency = ScheduleFrequency.Custom,
        DailyTime = "02:00",
        DailyTimeZoneId = "Europe/Paris",
        DurationMinutes = 5,
        IsPlanned = true,
        NextRunUtc = new DateTime(2026, 11, 10, 1, 0, 0, DateTimeKind.Utc),
        Movable = false,
        MachineReboot = new MachineRebootEntryDto
        {
            Source = MachineRebootSource.UnattendedUpgrades,
            CronExpression = "0 2 * * *",
            TimeZoneId = "Europe/Paris",
            Conditional = true,
            Detail = "/etc/apt/apt.conf.d/50unattended-upgrades"
        }
    };

    private static ScheduledServerActionDto WeeklyTimer() => DailyUnattended() with
    {
        Id = "machine-reboot:7:1",
        SourceId = 225,
        CronExpression = "30 3 * * 0",
        DailyTime = null,
        DailyTimeZoneId = null,
        MachineReboot = new MachineRebootEntryDto
        {
            Source = MachineRebootSource.SystemdTimer,
            CronExpression = "30 3 * * 0",
            TimeZoneId = "Europe/Paris",
            Detail = "/etc/systemd/system/weekly-reboot.timer"
        }
    };

    private static ScheduledServerActionDto ScheduledShutdown() => DailyUnattended() with
    {
        Id = "machine-reboot:7:2",
        SourceId = 226,
        CronExpression = string.Empty,
        DailyTime = null,
        DailyTimeZoneId = null,
        NextRunUtc = new DateTime(2026, 11, 9, 18, 0, 0, DateTimeKind.Utc),
        MachineReboot = new MachineRebootEntryDto
        {
            Source = MachineRebootSource.ScheduledShutdown,
            AtUtc = new DateTime(2026, 11, 9, 18, 0, 0, DateTimeKind.Utc),
            Detail = "/run/systemd/shutdown/scheduled"
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
    public void TheCard_SaysTheRebootIsManagedByTheMachine_ItsServer_AndThatItIsReadOnlyAndNotCounted()
    {
        var cut = RenderCard(DailyUnattended());

        var card = cut.Find("[data-testid='schedule-server-card-machine-reboot:7:0']");
        Assert.Contains("Reboot of srv-prod-01 managed by the machine", card.TextContent, StringComparison.Ordinal);
        Assert.Equal("Reboot managed by the machine", cut.Find("[data-testid='schedule-server-kind']").TextContent.Trim());
        Assert.Equal("Every day at 02:00 (Europe/Paris)", cut.Find("[data-testid='schedule-frequency']").TextContent.Trim());
        Assert.Equal("Automatic updates (unattended-upgrades) (only if a reboot is needed)",
            cut.Find("[data-testid='schedule-machine-reboot-source']").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-testid='schedule-read-only']"));
        Assert.NotNull(cut.Find("[data-testid='schedule-not-counted']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-overlap']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-no-overlap']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-cron']"));
    }

    [Fact]
    public void TheCard_ForANonDailyEntry_SaysItsScheduleAndZone_NotAGuessedDailyTime()
    {
        var cut = RenderCard(WeeklyTimer());

        Assert.Equal("On the schedule 30 3 * * 0 (Europe/Paris)", cut.Find("[data-testid='schedule-frequency']").TextContent.Trim());
        Assert.Equal("systemd timer", cut.Find("[data-testid='schedule-machine-reboot-source']").TextContent.Trim());
    }

    [Fact]
    public void TheCard_ForAShutdownAlreadyScheduled_SaysItIsOnceAndNamesTheNextRun()
    {
        // The date is rendered with the current culture ("g"): pin it, the CI agents run with the invariant culture.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            var cut = RenderCard(ScheduledShutdown());

            Assert.Equal("Once, already scheduled on the machine", cut.Find("[data-testid='schedule-frequency']").TextContent.Trim());
            Assert.Equal("Shutdown already scheduled on the machine", cut.Find("[data-testid='schedule-machine-reboot-source']").TextContent.Trim());
            Assert.Contains("09/11/2026 18:00", cut.Find("[data-testid='schedule-next']").TextContent, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TheInfo_SaysWhenWhatDecidesItAndTheFile_AndThatItCannotBeMoved()
    {
        var cut = RenderInfo(DailyUnattended());

        Assert.Equal("Every day at 02:00 (Europe/Paris)", cut.Find("[data-testid='schedule-info-machine-reboot-when']").TextContent.Trim());
        Assert.Equal("Automatic updates (unattended-upgrades) (only if a reboot is needed)",
            cut.Find("[data-testid='schedule-info-machine-reboot-source']").TextContent.Trim());
        Assert.Equal("/etc/apt/apt.conf.d/50unattended-upgrades", cut.Find("[data-testid='schedule-info-machine-reboot-detail']").TextContent.Trim());
        Assert.Equal("Decided by the machine itself and read by its agent: Aetheus does not order this reboot.",
            cut.Find("[data-testid='schedule-info-source']").TextContent.Trim());
        Assert.NotNull(cut.Find("[data-testid='schedule-info-read-only']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-info-movable']"));
    }

    [Fact]
    public void TheWords_AreInFrench_WithTheSameMeaning()
    {
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.French());
        var cut = RenderCard(DailyUnattended());

        Assert.Contains("Redémarrage de srv-prod-01 géré par la machine", cut.Find("[data-testid='schedule-server-card-machine-reboot:7:0']").TextContent, StringComparison.Ordinal);
        Assert.Equal("Redémarrage géré par la machine", cut.Find("[data-testid='schedule-server-kind']").TextContent.Trim());
        Assert.Equal(
            "Mises à jour automatiques (unattended-upgrades) (seulement si un redémarrage est nécessaire)",
            cut.Find("[data-testid='schedule-machine-reboot-source']").TextContent.Trim());
    }

    [Fact]
    public void TheRebootsGenre_HoldsTheMachinesOwnReboots_AndIsOfferedWhenOnlyTheyArePresent()
    {
        var plan = new SchedulePlanDto { ServerActions = [DailyUnattended()] };

        Assert.Equal(ScheduleGenres.Reboot, ScheduleGenres.Of(ServerActionKind.MachineReboot));
        Assert.Contains(ScheduleGenres.Reboot, PipelineScheduleFilter.Genres(plan, chosen: null));
        Assert.Single(PipelineScheduleFilter.Apply(plan, ScheduleGenres.Reboot, serverId: null).ServerActions);
        Assert.Empty(PipelineScheduleFilter.Apply(plan, ScheduleGenres.Scan, serverId: null).ServerActions);
        Assert.DoesNotContain(ScheduleGenres.Reboot, PipelineScheduleFilter.Genres(new SchedulePlanDto(), chosen: null));
    }

    [Fact]
    public void TwoEntriesOfOneServer_GetTwoDistinctPlannerRows()
    {
        Assert.NotEqual(
            ServerActionPlannerTask.RowId(DailyUnattended()),
            ServerActionPlannerTask.RowId(WeeklyTimer()));
    }
}
