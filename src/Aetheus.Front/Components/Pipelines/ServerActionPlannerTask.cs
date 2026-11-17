// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>A drop on the calendar that changes the schedule of a scheduled action (scan, AI task, backup), as the page
/// writes it: the action, the expression to store (in the form of its sort), the sentence the success toast says and the
/// expression "Undo" stores back. For the CVE watch both expressions are its local daily time, <c>HH:mm</c>.</summary>
public sealed record ServerActionMoveWrite(ScheduledServerActionDto Action, string CronExpression, string Detail, string UndoCronExpression);

/// <summary>
/// A server action seen as a planner row. The plan's tiles, the overlap pairs, the local-time words and the rules of
/// a drop (<see cref="ScheduleMovePlanner"/>) are written for <see cref="ScheduledTaskDto"/>; an action is handed to
/// them as one rather than copying them, with a negative <see cref="ScheduledTaskDto.PipelineId"/> that no pipeline has
/// (<see cref="RowId"/>), and its title as its name. The row is never sent anywhere and never stored.
/// </summary>
internal static class ServerActionPlannerTask
{
    /// <summary>The planner row id of an action: negative, one per source and sort, so it never meets a pipeline id.</summary>
    public static int RowId(ScheduledServerActionDto action) => -((action.SourceId * 10) + (int)action.Kind + 1);

    public static ScheduledTaskDto AsTask(ScheduledServerActionDto action, string title) => new()
    {
        PipelineId = RowId(action),
        PipelineName = title,
        Enabled = true,
        CronExpression = action.CronExpression,
        Frequency = action.Frequency,
        Day = action.Day,
        Hour = action.Hour,
        Minute = action.Minute,
        DurationMinutes = action.DurationMinutes,
        IsPlanned = action.IsPlanned,
        NextRunUtc = action.NextRunUtc,
        Occurrences = action.Occurrences,
        OccurrencesTruncated = action.OccurrencesTruncated
    };

    /// <summary>The name of an action on the page: what it does and where, "Rkhunter scan on srv-prod-01", "AI task nightly
    /// review", "Backup orders-db".</summary>
    public static string Title(IStringLocalizer<AppStrings> localizer, ScheduledServerActionDto action) =>
        string.Format(
            localizer[Pick(action.Kind, "ScheduleServerActionRkhunterScan", "ScheduleServerActionAiTask", "ScheduleServerActionAppBackup", "ScheduleServerActionRestoreCheck", "ScheduleServerActionCveWatch", "ScheduleServerActionReboot", "ScheduleServerActionMachineReboot", "ScheduleServerActionMachineJob")].Value,
            action.Kind is ServerActionKind.RkhunterScan or ServerActionKind.ServerReboot or ServerActionKind.MachineReboot ? action.ServerName : action.SourceName);

    /// <summary>The page where the action's schedule is kept: the rkhunter section of its server, the AI tasks of its
    /// project (all of them for a server's task), the backups.</summary>
    public static string Link(ScheduledServerActionDto action) => action.Kind switch
    {
        ServerActionKind.RkhunterScan => string.Create(CultureInfo.InvariantCulture, $"/servers/{action.ServerId}/rkhunter"),
        ServerActionKind.AiTask => action.ProjectId is { } project
            ? string.Create(CultureInfo.InvariantCulture, $"/projects/{project}/ai-tasks")
            : "/ai-tasks",
        ServerActionKind.AppBackup or ServerActionKind.RestoreCheck => "/backups",
        ServerActionKind.CveWatch => "/admin/cve-sources",
        ServerActionKind.ServerReboot or ServerActionKind.MachineReboot or ServerActionKind.MachineJob => string.Create(CultureInfo.InvariantCulture, $"/servers/{action.ServerId}"),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Every kind of action has its own page.")
    };

    /// <summary>The page of the server an action runs on, when it names one (an AI task of a project names none).</summary>
    public static string? ServerLink(ScheduledServerActionDto action) =>
        action.ServerId > 0 ? string.Create(CultureInfo.InvariantCulture, $"/servers/{action.ServerId}") : null;

    /// <summary>The page of the project that owns an AI task or a backup.</summary>
    public static string? ProjectLink(ScheduledServerActionDto action) =>
        action.ProjectId is { } project ? string.Create(CultureInfo.InvariantCulture, $"/projects/{project}") : null;

    public static OmniIconName Icon(ServerActionKind kind) => kind switch
    {
        ServerActionKind.RkhunterScan => OmniIconName.HardDrive,
        ServerActionKind.AiTask => OmniIconName.Robot,
        ServerActionKind.AppBackup => OmniIconName.Archive,
        ServerActionKind.RestoreCheck => OmniIconName.Restore,
        ServerActionKind.CveWatch => OmniIconName.ShieldWarning,
        ServerActionKind.ServerReboot or ServerActionKind.MachineReboot => OmniIconName.Sync,
        ServerActionKind.MachineJob => OmniIconName.TerminalWindow,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Every kind of action has its own icon.")
    };

    /// <summary>The badge that names the sort of action ("Server action", "AI task", "Backup").</summary>
    public static string KindKey(ServerActionKind kind) => Pick(kind, "ScheduleKindServerAction", "ScheduleKindAiTask", "ScheduleKindAppBackup", "ScheduleKindRestoreCheck", "ScheduleKindCveWatch", "ScheduleKindServerAction", "ScheduleKindMachineReboot", "ScheduleKindMachineJob");

    /// <summary>The menu entry that opens the page where the schedule is kept.</summary>
    public static string OpenKey(ServerActionKind kind) => Pick(kind, "ScheduleOpenServer", "ScheduleOpenAiTasks", "ScheduleOpenBackups", "ScheduleOpenBackups", "ScheduleOpenCveSources", "ScheduleOpenServer", "ScheduleOpenServer", "ScheduleOpenServer");

    /// <summary>Where the schedule comes from, said in the block's info.</summary>
    public static string SourceKey(ServerActionKind kind) =>
        Pick(kind, "ScheduleServerActionSource", "ScheduleAiTaskSource", "ScheduleAppBackupSource", "ScheduleRestoreCheckSource", "ScheduleCveWatchSource", "ScheduleServerRebootSource", "ScheduleMachineRebootSource", "ScheduleMachineJobSource");

    /// <summary>What a move writes, said in the block's info of a movable action.</summary>
    public static string MoveTextKey(ServerActionKind kind) =>
        Pick(kind, "ScheduleServerActionMoveText", "ScheduleAiTaskMoveText", "ScheduleAppBackupMoveText", "ScheduleRestoreCheckReadOnlyReason", "ScheduleCveWatchMoveText", "ScheduleServerRebootReadOnlyReason", "ScheduleMachineRebootReadOnlyReason", "ScheduleMachineJobReadOnlyReason");

    /// <summary>Why the reader cannot move the action: the right it lacks.</summary>
    public static string ReadOnlyKey(ServerActionKind kind) =>
        Pick(kind, "ScheduleServerActionReadOnlyReason", "ScheduleAiTaskReadOnlyReason", "ScheduleAppBackupReadOnlyReason", "ScheduleRestoreCheckReadOnlyReason", "ScheduleCveWatchReadOnlyReason", "ScheduleServerRebootReadOnlyReason", "ScheduleMachineRebootReadOnlyReason", "ScheduleMachineJobReadOnlyReason");

    /// <summary>Why an action with a custom schedule is not moved by a drop: it is changed where it is kept.</summary>
    public static string MoveCustomKey(ServerActionKind kind) =>
        Pick(kind, "ScheduleServerActionMoveCustom", "ScheduleAiTaskMoveCustom", "ScheduleAppBackupMoveCustom", "ScheduleRestoreCheckReadOnlyReason", "ScheduleCveWatchReadOnlyReason", "ScheduleServerRebootReadOnlyReason", "ScheduleMachineRebootReadOnlyReason", "ScheduleMachineJobReadOnlyReason");

    private static string Pick(ServerActionKind kind, string scan, string ai, string backup, string restoreCheck, string cveWatch, string reboot, string machineReboot, string machineJob) => kind switch
    {
        ServerActionKind.RkhunterScan => scan,
        ServerActionKind.AiTask => ai,
        ServerActionKind.AppBackup => backup,
        ServerActionKind.RestoreCheck => restoreCheck,
        ServerActionKind.CveWatch => cveWatch,
        ServerActionKind.ServerReboot => reboot,
        ServerActionKind.MachineReboot => machineReboot,
        ServerActionKind.MachineJob => machineJob,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Every kind of action has its own words.")
    };
}

