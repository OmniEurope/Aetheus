// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>The sort of scheduled action the planner shows beside the pipelines (PLAN-005, recette R-8). Only what Aetheus
/// itself schedules and can read is listed: the scan of a server, the AI tasks and the backup policies; the other sorts
/// (cron tasks, Certbot renewal) join when their reading exists. The value is part of the planner row id: never reorder.</summary>
public enum ServerActionKind
{
    /// <summary>The scheduled rootkit scan of a server (the schedule of its rkhunter section).</summary>
    RkhunterScan = 0,

    /// <summary>A scheduled AI task (the six-field schedule, seconds first, of its definition, UTC).</summary>
    AiTask = 1,

    /// <summary>The scheduled backup of an application backup policy (five-field cron, UTC).</summary>
    AppBackup = 2,

    /// <summary>The restore-check of an application backup policy (five-field cron, UTC): the check of the latest
    /// successful backup. Read only here, its cadence is changed in the policy.</summary>
    RestoreCheck = 3,

    /// <summary>The periodic refresh of the vulnerability feeds (the CVE watch). Site-wide, read only, listed for site
    /// administrators only; it runs once a day at a configured local time, not a cron.</summary>
    CveWatch = 4,

    /// <summary>The daily reboot of a server, ordered by Aetheus and executed by its agent (recette R-8, option B of
    /// 2026-11-07). Its time is kept in the time zone of the schedule (<see cref="ScheduledServerActionDto.TimeZoneId"/>).</summary>
    ServerReboot = 5,

    /// <summary>A reboot the machine decides by itself (unattended-upgrades, a cron entry, a systemd timer, a scheduled
    /// shutdown), read by its agent (recette R-11, option A of 2026-11-09). Read only, never ordered by Aetheus, and it uses
    /// no runner: drawn, never counted in the overlaps.</summary>
    MachineReboot = 6,

    /// <summary>A job the machine schedules by itself (a crontab line, a run-parts directory, a whitelisted systemd timer),
    /// read by its agent (recette R-27, option A of 2026-11-12). Read only, never run by Aetheus, and it uses no runner:
    /// drawn, never counted in the overlaps.</summary>
    MachineJob = 7
}

public static class ServerActionDefaults
{
    /// <summary>The duration of an AI task or a backup that never completed: one hour, as for a pipeline.</summary>
    public const int OtherPlannedMinutes = ScheduleDefaults.PlannedMinutes;

    /// <summary>The planned duration of a sort of action that never completed.</summary>
    public static int PlannedMinutes(ServerActionKind kind) => kind switch
    {
        ServerActionKind.RkhunterScan => RkhunterPlannedMinutes,
        ServerActionKind.RestoreCheck => RestoreCheckPlannedMinutes,
        ServerActionKind.CveWatch => CveWatchPlannedMinutes,
        ServerActionKind.MachineJob => MachineJobPlannedMinutes,
        ServerActionKind.ServerReboot or ServerActionKind.MachineReboot => Servers.ServerRebootOrder.PlannedMinutes,
        _ => OtherPlannedMinutes
    };

    /// <summary>The expression as the planner reads it (six fields, seconds first) from the one stored for a sort of
    /// action: an AI task stores six fields already, a scan and a backup five.</summary>
    public static string ToEngineFields(ServerActionKind kind, string stored) =>
        kind == ServerActionKind.AiTask ? stored.Trim() : ToEngineFields(stored);

    /// <summary>The expression to store for a sort of action from the six-field one <see cref="ScheduleFrequencyCodec"/> writes.</summary>
    public static string ToStoredFields(ServerActionKind kind, string engineSixFields) =>
        kind == ServerActionKind.AiTask ? engineSixFields : ToStoredFields(engineSixFields);

    /// <summary>Duration assumed for a scan that never completed: the scan task times out after 300 seconds
    /// (<c>RkhunterSchedulerService</c>).</summary>
    public const int RkhunterPlannedMinutes = 5;

    /// <summary>Duration assumed for a restore-check that never completed: the restore-check task times out after 1800
    /// seconds (<c>BackupPolicyService</c>).</summary>
    public const int RestoreCheckPlannedMinutes = 30;

    /// <summary>Duration assumed for a pass of the CVE watch when no source recorded a duration yet.</summary>
    public const int CveWatchPlannedMinutes = 5;

    /// <summary>Duration drawn for a job the machine schedules by itself: the agent cannot know how long it runs, so a short
    /// marker (a block of one hour per cron line would hide the pipelines).</summary>
    public const int MachineJobPlannedMinutes = 5;

    /// <summary>Whether the planner can ever move a sort of action. The restore-check cadence and the CVE watch are read
    /// only by design (changed in the backup policy, set in the configuration), whatever the rights of the reader.</summary>
    public static bool CanBeMoved(ServerActionKind kind) =>
        kind is not (ServerActionKind.RestoreCheck or ServerActionKind.CveWatch or ServerActionKind.ServerReboot
            or ServerActionKind.MachineReboot or ServerActionKind.MachineJob);

    /// <summary>Whether a sort of action competes with the pipelines for the execution capacity (recette R-8). The CVE
    /// watch downloads feeds on the Aetheus server itself and the machine's own reboots and jobs are decided and run by the
    /// host: none uses a runner, they are drawn, never counted in the overlaps.</summary>
    public static bool CountsInOverlaps(ServerActionKind kind) =>
        kind is not (ServerActionKind.CveWatch or ServerActionKind.MachineReboot or ServerActionKind.MachineJob);

    /// <summary>The stored schedule is a five-field cron expression (minutes first, UTC); the planner's codec and
    /// expander read six fields (seconds first). These two conversions are the only place the difference is known.</summary>
    public static string ToEngineFields(string storedFiveFields) => "0 " + storedFiveFields.Trim();

    /// <summary>The five-field expression to store for a six-field one written by <see cref="ScheduleFrequencyCodec"/>
    /// (always at second zero).</summary>
    public static string ToStoredFields(string engineSixFields) =>
        engineSixFields.StartsWith("0 ", StringComparison.Ordinal) ? engineSixFields[2..] : engineSixFields;
}

/// <summary>One scheduled action (a server scan, an AI task, a backup), as the planner draws it next to the pipelines.</summary>
public sealed record ScheduledServerActionDto
{
    /// <summary>Stable identity by sort and source: <c>rkhunter:7</c> (server 7), <c>ai:3</c> (AI task 3), <c>backup:5</c> (policy 5).</summary>
    public string Id { get; init; } = string.Empty;

    public ServerActionKind Kind { get; init; }

    /// <summary>What is scheduled: the server for a scan, the AI task, the backup policy.</summary>
    public int SourceId { get; init; }

    /// <summary>The name of the AI task or of the backup policy; empty for a scan (named by its server).</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>The server the action runs on: the scanned one, the backup's target, the server owning an AI task; 0 and an
    /// empty name for an AI task owned by a project (its execution server is chosen at each run).</summary>
    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;

    /// <summary>The owning project of an AI task or a backup; null for a scan and for an AI task owned by a server.</summary>
    public int? ProjectId { get; init; }
    public string? ProjectName { get; init; }

    /// <summary>The expression as stored: five fields (minutes first) for a scan and a backup, six (seconds first) for an
    /// AI task; UTC.</summary>
    public string CronExpression { get; init; } = string.Empty;

    public ScheduleFrequency Frequency { get; init; } = ScheduleFrequency.Custom;

    /// <summary>Day of week (0 = Sunday) for <see cref="ScheduleFrequency.Weekly"/>, day of month for
    /// <see cref="ScheduleFrequency.Monthly"/>, otherwise null.</summary>
    public int? Day { get; init; }

    /// <summary>UTC hour and minute; null for an expression that is not a single time.</summary>
    public int? Hour { get; init; }
    public int? Minute { get; init; }

    /// <summary>Average of the last successful runs of the action, or the planned default of its sort when there is none.</summary>
    public int DurationMinutes { get; init; } = ServerActionDefaults.RkhunterPlannedMinutes;

    /// <summary>True when <see cref="DurationMinutes"/> is the planned default (nothing completed yet).</summary>
    public bool IsPlanned { get; init; } = true;

    public DateTime? NextRunUtc { get; init; }
    public List<ScheduleOccurrenceDto> Occurrences { get; init; } = [];

    /// <summary>True when the expression fires more often than <see cref="Occurrences"/> can hold.</summary>
    public bool OccurrencesTruncated { get; init; }

    /// <summary>True when the reader may rewrite this schedule: the Administrator right on the server for a scan, the Write
    /// right on the owner for an AI task and on the project for a backup. The write itself is checked again by the
    /// source's own endpoint.</summary>
    public bool Movable { get; init; }

    /// <summary>For the CVE watch, the local time of its one daily pass, <c>HH:mm</c> (the service runs once a day at a
    /// configured time, not a cron: <see cref="CronExpression"/> is then empty); null for every other sort.</summary>
    public string? DailyTime { get; init; }

    /// <summary>The IANA time zone <see cref="DailyTime"/> is read in (<c>Europe/Paris</c>); null with it.</summary>
    public string? DailyTimeZoneId { get; init; }

    /// <summary>For a reboot: the schedule with its time zone and what the scheduler last did (planned, done, postponed,
    /// skipped); null for every other sort. A reboot is changed in the server page, never by a drag.</summary>
    public Servers.ServerRebootScheduleDto? Reboot { get; init; }

    /// <summary>For a reboot the machine decides by itself: what its agent read (the source, the file or unit, whether the
    /// reboot is conditional); null for every other sort. Its time is <see cref="DailyTime"/> for a daily one, otherwise the
    /// entry's own expression and zone.</summary>
    public Servers.MachineRebootEntryDto? MachineReboot { get; init; }

    /// <summary>For a job the machine schedules by itself: what its agent read (the source, the program, the user, the file
    /// or unit); null for every other sort. Its time is <see cref="DailyTime"/> for a daily one, otherwise the job's own
    /// expression and zone, or the next instant of a timer.</summary>
    public Servers.MachineJobEntryDto? MachineJob { get; init; }
}
