// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class ServerActionScheduleRepository(AppDbContext db) : IServerActionScheduleRepository
{
    public const int AverageSampleSize = 10;

    // A server belongs to its organization directly (Server.OrganizationId): the same scope the execution capacity uses.
    public Task<List<RkhunterScheduleRow>> GetRkhunterSchedulesAsync(
        int organizationId, IReadOnlyCollection<int>? readableServerIds, CancellationToken ct)
    {
        var states = db.RkhunterStates.AsNoTracking()
            .Where(state => state.Server.OrganizationId == organizationId
                && state.ScanScheduleCron != null && state.ScanScheduleCron != string.Empty);
        if (readableServerIds is not null)
        {
            states = states.Where(state => readableServerIds.Contains(state.ServerId));
        }

        return states
            .OrderBy(state => state.Server.Name)
            .ThenBy(state => state.ServerId)
            .Select(state => new RkhunterScheduleRow(state.ServerId, state.Server.Name, state.ScanScheduleCron!))
            .ToListAsync(ct);
    }

    public async Task<Dictionary<int, int>> GetRkhunterAverageMinutesAsync(IReadOnlyCollection<int> serverIds, CancellationToken ct)
    {
        if (serverIds.Count == 0)
        {
            return [];
        }

        var ids = serverIds.ToList();
        var rows = await db.Servers
            .AsNoTracking()
            .Where(server => ids.Contains(server.Id))
            .Select(server => new
            {
                server.Id,
                Scans = db.Tasks
                    .Where(task => task.ServerId == server.Id && task.Operation == OperationKind.RkhunterScan
                        && task.Status == TaskExecutionStatus.Success && task.StartedAt != null && task.CompletedAt != null)
                    .OrderByDescending(task => task.CompletedAt)
                    .ThenByDescending(task => task.Id)
                    .Take(AverageSampleSize)
                    .Select(task => new { StartedAt = task.StartedAt!.Value, CompletedAt = task.CompletedAt!.Value })
                    .ToList()
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Where(row => row.Scans.Count > 0)
            .ToDictionary(
                row => row.Id,
                row => Math.Max(1, (int)Math.Ceiling(row.Scans.Average(scan => (scan.CompletedAt - scan.StartedAt).TotalMinutes))));
    }

    // An AI task belongs to its organization through its project or its server, the two owners the task endpoints know.
    // A null list lifts the restriction on that owner only: a wildcard on the projects does not show a server's tasks.
    public Task<List<AiTaskScheduleRow>> GetAiTaskSchedulesAsync(
        int organizationId, IReadOnlyCollection<int>? readableProjectIds, IReadOnlyCollection<int>? readableServerIds, CancellationToken ct)
    {
        var anyProject = readableProjectIds is null;
        var anyServer = readableServerIds is null;
        var projectIds = readableProjectIds?.ToList() ?? [];
        var serverIds = readableServerIds?.ToList() ?? [];
        return db.AiTaskDefinitions.AsNoTracking()
            .Where(task => task.Enabled && task.Schedule != null && task.Schedule != string.Empty)
            .Where(task => (task.ProjectId != null && task.Project!.OrganizationId == organizationId && (anyProject || projectIds.Contains(task.ProjectId.Value)))
                || (task.ServerId != null && task.Server!.OrganizationId == organizationId && (anyServer || serverIds.Contains(task.ServerId.Value))))
            .OrderBy(task => task.Name)
            .ThenBy(task => task.Id)
            .Select(task => new AiTaskScheduleRow(
                task.Id, task.Name, task.Schedule!, task.ProjectId, task.Project != null ? task.Project.Name : null,
                task.ServerId, task.Server != null ? task.Server.Name : null))
            .ToListAsync(ct);
    }

    public async Task<Dictionary<int, int>> GetAiTaskAverageMinutesAsync(IReadOnlyCollection<int> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0)
        {
            return [];
        }

        var ids = taskIds.ToList();
        var rows = await db.AiTaskDefinitions
            .AsNoTracking()
            .Where(task => ids.Contains(task.Id))
            .Select(task => new
            {
                task.Id,
                Runs = db.AiRunResults
                    .Where(result => result.AiTaskDefinitionId == task.Id && result.Succeeded && result.DurationMs > 0)
                    .OrderByDescending(result => result.CreatedAt)
                    .ThenByDescending(result => result.Id)
                    .Take(AverageSampleSize)
                    .Select(result => result.DurationMs)
                    .ToList()
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Where(row => row.Runs.Count > 0)
            .ToDictionary(row => row.Id, row => Math.Max(1, (int)Math.Ceiling(row.Runs.Average() / 60000d)));
    }

    // A backup policy belongs to its organization through its project (the scope the policy endpoints use).
    public Task<List<BackupScheduleRow>> GetBackupSchedulesAsync(
        int organizationId, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct)
    {
        var policies = db.BackupPolicies.AsNoTracking()
            .Where(policy => policy.Enabled && policy.ScheduleCron != string.Empty && policy.Project.OrganizationId == organizationId);
        if (readableProjectIds is not null)
        {
            var projectIds = readableProjectIds.ToList();
            policies = policies.Where(policy => projectIds.Contains(policy.ProjectId));
        }

        return policies
            .OrderBy(policy => policy.Name)
            .ThenBy(policy => policy.Id)
            .Select(policy => new BackupScheduleRow(
                policy.Id, policy.Name, policy.ScheduleCron, policy.ProjectId, policy.Project.Name, policy.ServerId, policy.Server.Name))
            .ToListAsync(ct);
    }

    public async Task<Dictionary<int, int>> GetBackupAverageMinutesAsync(IReadOnlyCollection<int> policyIds, CancellationToken ct)
    {
        if (policyIds.Count == 0)
        {
            return [];
        }

        var ids = policyIds.ToList();
        var rows = await db.BackupPolicies
            .AsNoTracking()
            .Where(policy => ids.Contains(policy.Id))
            .Select(policy => new
            {
                policy.Id,
                Runs = db.BackupRuns
                    .Where(run => run.BackupPolicyId == policy.Id && run.Status == BackupRunStatus.Succeeded && run.CompletedAt != null)
                    .OrderByDescending(run => run.CompletedAt)
                    .ThenByDescending(run => run.Id)
                    .Take(AverageSampleSize)
                    .Select(run => new { run.StartedAt, CompletedAt = run.CompletedAt!.Value })
                    .ToList()
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Where(row => row.Runs.Count > 0)
            .ToDictionary(
                row => row.Id,
                row => Math.Max(1, (int)Math.Ceiling(row.Runs.Average(run => (run.CompletedAt - run.StartedAt).TotalMinutes))));
    }

    // A restore-check cadence is a column of the policy: same scope as the backup (its project), only policies that have one.
    public Task<List<RestoreCheckScheduleRow>> GetRestoreCheckSchedulesAsync(
        int organizationId, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct)
    {
        var policies = db.BackupPolicies.AsNoTracking()
            .Where(policy => policy.Enabled && policy.RestoreCheckCron != null && policy.RestoreCheckCron != string.Empty
                && policy.Project.OrganizationId == organizationId);
        if (readableProjectIds is not null)
        {
            var projectIds = readableProjectIds.ToList();
            policies = policies.Where(policy => projectIds.Contains(policy.ProjectId));
        }

        return policies
            .OrderBy(policy => policy.Name)
            .ThenBy(policy => policy.Id)
            .Select(policy => new RestoreCheckScheduleRow(
                policy.Id, policy.Name, policy.RestoreCheckCron!, policy.ProjectId, policy.Project.Name, policy.ServerId, policy.Server.Name))
            .ToListAsync(ct);
    }

    // The feeds are read one after the other, so a pass lasts what the latest successful pass of each feed lasted. An administrator's
    // offline import (trigger Import) is no pass of the service: its duration, which can be very long for a dump of hundreds of
    // megabytes, is not what the next scheduled pass will take, and the state's own last duration mixes both.
    public async Task<long> GetCveWatchPassDurationMsAsync(CancellationToken ct)
    {
        var passes = await db.VulnerabilityFeedPasses.AsNoTracking()
            .Where(pass => pass.Succeeded && pass.Trigger != VulnerabilityFeedPassTrigger.Import)
            .Select(pass => new { pass.Source, pass.StartedAt, pass.EndedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        return (long)passes
            .GroupBy(pass => pass.Source, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(pass => pass.StartedAt).First())
            .Sum(pass => (pass.EndedAt - pass.StartedAt).TotalMilliseconds);
    }
}
