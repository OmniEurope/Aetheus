// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Recette R-27: the words of a job the machine schedules by itself (a crontab line, a run-parts directory, a
/// whitelisted systemd timer), in the planner's card and in the block's info. Aetheus reads the host's own schedule: it never
/// runs, moves or changes the job.</summary>
internal static class MachineJobText
{
    /// <summary>When it runs: "Every day at 02:00 (Europe/Paris)", the expression and its zone, or "Next run only" for a timer
    /// that has no calendar.</summary>
    public static string When(IStringLocalizer<AppStrings> localizer, ScheduledServerActionDto action)
    {
        if (action.DailyTime is { } time) return string.Format(localizer["ScheduleCveDaily"].Value, time, action.DailyTimeZoneId);
        return action.MachineJob is { AtUtc: null } job
            ? string.Format(localizer["ScheduleMachineJobCron"].Value, job.CronExpression, job.TimeZoneId)
            : localizer["ScheduleMachineJobNextOnly"].Value;
    }

    /// <summary>What schedules it (the system crontab, a cron.d file, a user crontab, a run-parts directory, a systemd timer),
    /// the user it runs as, and "earliest time" when anacron may run it later.</summary>
    public static string Source(IStringLocalizer<AppStrings> localizer, MachineJobEntryDto job)
    {
        var source = localizer[job.Source switch
        {
            MachineJobSource.SystemCrontab => "MachineJobSourceSystemCrontab",
            MachineJobSource.CronDirectory => "MachineJobSourceCronDirectory",
            MachineJobSource.UserCrontab => "MachineJobSourceUserCrontab",
            MachineJobSource.PeriodicDirectory => "MachineJobSourcePeriodicDirectory",
            MachineJobSource.SystemdTimer => "MachineJobSourceSystemdTimer",
            _ => throw new ArgumentOutOfRangeException(nameof(job), job.Source, "Every source of a machine job has its own words.")
        }].Value;
        if (job.User is { Length: > 0 } user) source = $"{source}, {string.Format(localizer["MachineJobRunsAs"].Value, user)}";
        return job.Conditional ? $"{source} ({localizer["MachineJobConditional"].Value})" : source;
    }
}
