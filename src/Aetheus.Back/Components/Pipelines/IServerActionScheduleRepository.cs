// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>A server that has a scan schedule: its name and the five-field cron expression stored for it.</summary>
internal sealed record RkhunterScheduleRow(int ServerId, string ServerName, string CronExpression);

/// <summary>An enabled AI task that has a schedule: the six-field expression (seconds first) stored for it, and its owner,
/// a project (<see cref="ProjectId"/>) or a server (<see cref="ServerId"/>), with the owner's name.</summary>
internal sealed record AiTaskScheduleRow(
    int TaskId, string TaskName, string Schedule, int? ProjectId, string? ProjectName, int? ServerId, string? ServerName);

/// <summary>An enabled backup policy: its five-field cron expression, its project and its target server.</summary>
internal sealed record BackupScheduleRow(
    int PolicyId, string PolicyName, string ScheduleCron, int ProjectId, string ProjectName, int ServerId, string ServerName);

/// <summary>An enabled backup policy that has a restore-check cadence: its five-field cron expression, its project and its
/// target server.</summary>
internal sealed record RestoreCheckScheduleRow(
    int PolicyId, string PolicyName, string RestoreCheckCron, int ProjectId, string ProjectName, int ServerId, string ServerName);

/// <summary>What the planner needs of the scheduled actions (recette R-8), without the columns it does not.</summary>
internal interface IServerActionScheduleRepository
{
    /// <summary>The servers of the organization that have a scan schedule, limited to
    /// <paramref name="readableServerIds"/> (null: no restriction), by server name.</summary>
    Task<List<RkhunterScheduleRow>> GetRkhunterSchedulesAsync(
        int organizationId, IReadOnlyCollection<int>? readableServerIds, CancellationToken ct);

    /// <summary>The average duration, in minutes (rounded up, at least 1), of the last ten successful scans of each
    /// server, in a single query. A server with no completed successful scan is absent from the result.</summary>
    Task<Dictionary<int, int>> GetRkhunterAverageMinutesAsync(IReadOnlyCollection<int> serverIds, CancellationToken ct);

    /// <summary>The enabled AI tasks that have a schedule and belong, through their project or their server, to the
    /// organization. A task is kept when its project is in <paramref name="readableProjectIds"/> or its server in
    /// <paramref name="readableServerIds"/> (null: no restriction on that owner), by task name.</summary>
    Task<List<AiTaskScheduleRow>> GetAiTaskSchedulesAsync(
        int organizationId, IReadOnlyCollection<int>? readableProjectIds, IReadOnlyCollection<int>? readableServerIds, CancellationToken ct);

    /// <summary>The average duration, in minutes (rounded up, at least 1), of the last ten successful runs of each AI task.
    /// A task with no successful run is absent from the result.</summary>
    Task<Dictionary<int, int>> GetAiTaskAverageMinutesAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct);

    /// <summary>The enabled backup policies of the organization (through their project) that have a schedule, limited to
    /// <paramref name="readableProjectIds"/> (null: no restriction), by policy name.</summary>
    Task<List<BackupScheduleRow>> GetBackupSchedulesAsync(
        int organizationId, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct);

    /// <summary>The average duration, in minutes (rounded up, at least 1), of the last ten successful backups of each
    /// policy. A policy with no completed successful backup is absent from the result.</summary>
    Task<Dictionary<int, int>> GetBackupAverageMinutesAsync(IReadOnlyCollection<int> policyIds, CancellationToken ct);

    /// <summary>The enabled backup policies of the organization (through their project) that have a restore-check cadence,
    /// limited to <paramref name="readableProjectIds"/> (null: no restriction), by policy name.</summary>
    Task<List<RestoreCheckScheduleRow>> GetRestoreCheckSchedulesAsync(
        int organizationId, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct);

    /// <summary>How long a pass of the CVE watch lasts (global data, like the advisories), in milliseconds: the latest
    /// successful pass of each feed, summed, offline imports left out; 0 when no feed has a successful pass.</summary>
    Task<long> GetCveWatchPassDurationMsAsync(CancellationToken ct);
}
