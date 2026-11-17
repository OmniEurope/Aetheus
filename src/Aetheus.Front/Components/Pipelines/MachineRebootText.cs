// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Recette R-11: the words of a reboot the machine decides by itself, in the planner's card and in the block's
/// info. The wording is always "managed by the machine": Aetheus reads the host's own schedule, it never orders this reboot.</summary>
internal static class MachineRebootText
{
    /// <summary>When it happens: "Every day at 02:00 (Europe/Paris)", the expression and its zone, or "Once".</summary>
    public static string When(IStringLocalizer<AppStrings> localizer, ScheduledServerActionDto action)
    {
        if (action.DailyTime is { } time) return string.Format(localizer["ScheduleCveDaily"].Value, time, action.DailyTimeZoneId);
        return action.MachineReboot is { AtUtc: null } entry
            ? string.Format(localizer["ScheduleMachineRebootCron"].Value, entry.CronExpression, entry.TimeZoneId)
            : localizer["ScheduleMachineRebootOnce"].Value;
    }

    /// <summary>What decides it: unattended-upgrades, a cron entry, a systemd timer or a shutdown already scheduled, followed
    /// by "only if needed" when the reboot depends on something else.</summary>
    public static string Source(IStringLocalizer<AppStrings> localizer, MachineRebootEntryDto entry)
    {
        var source = localizer[entry.Source switch
        {
            MachineRebootSource.UnattendedUpgrades => "MachineRebootSourceUnattendedUpgrades",
            MachineRebootSource.Cron => "MachineRebootSourceCron",
            MachineRebootSource.SystemdTimer => "MachineRebootSourceSystemdTimer",
            MachineRebootSource.ScheduledShutdown => "MachineRebootSourceScheduledShutdown",
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Source, "Every source of a machine reboot has its own words.")
        }].Value;
        return entry.Conditional ? $"{source} ({localizer["MachineRebootConditional"].Value})" : source;
    }
}
