// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>A schedule with what its views need of its server and of its last order task.</summary>
internal sealed record ServerRebootScheduleRow(
    ServerRebootSchedule Schedule, string ServerName, int OrganizationId, string? AgentCapabilitiesJson,
    TaskExecutionStatus? LastTaskStatus, DateTime? LastTaskCompletedAtUtc, string? LastTaskFailureReason);

/// <summary>What the agent last reported of the reboots a server decides by itself (recette R-11), as stored.</summary>
internal sealed record MachineRebootRow(int ServerId, string ServerName, string Json);

/// <summary>What the agent last reported of the jobs a server schedules by itself and of its time zone (recette R-27), as stored.</summary>
internal sealed record MachineJobsRow(int ServerId, string ServerName, string Json);

/// <summary>The reboot order task as the agent's confirmation reads it.</summary>
internal sealed record RebootOrderTaskRow(int TaskId, int ServerId, OperationKind Operation, TaskExecutionStatus Status, string EnvironmentVariables);

/// <summary>EF access of the server reboot schedules (recette R-8, option B of 2026-11-07).</summary>
internal interface IServerRebootRepository
{
    /// <summary>The tracked schedule of a server, or null.</summary>
    Task<ServerRebootSchedule?> FindAsync(int serverId, CancellationToken ct);

    Task<ServerRebootScheduleRow?> GetRowAsync(int serverId, CancellationToken ct);

    /// <summary>The schedules of an organization, limited to <paramref name="serverIds"/> (null: no restriction).</summary>
    Task<List<ServerRebootScheduleRow>> GetRowsAsync(int organizationId, IReadOnlyCollection<int>? serverIds, CancellationToken ct);

    /// <summary>The stored readings of the reboots the machines decide by themselves, for the servers of an organization that
    /// have one, limited to <paramref name="serverIds"/> (null: no restriction).</summary>
    Task<List<MachineRebootRow>> GetMachineRebootsAsync(int organizationId, IReadOnlyCollection<int>? serverIds, CancellationToken ct);

    /// <summary>The stored readings of the jobs the machines schedule by themselves (recette R-27), for the servers of an
    /// organization that have one, limited to <paramref name="serverIds"/> (null: no restriction).</summary>
    Task<List<MachineJobsRow>> GetMachineJobsAsync(int organizationId, IReadOnlyCollection<int>? serverIds, CancellationToken ct);

    /// <summary>The tracked enabled schedules, for the scheduler.</summary>
    Task<List<ServerRebootSchedule>> GetEnabledAsync(CancellationToken ct);

    /// <summary>The name of a task assigned to or running on the server (a pipeline step or a server action), other
    /// than <paramref name="exceptTaskId"/>; null when the server is idle.</summary>
    Task<string?> FindBusyTaskNameAsync(int serverId, int? exceptTaskId, CancellationToken ct);

    Task<RebootOrderTaskRow?> GetOrderTaskAsync(int taskId, CancellationToken ct);

    Task<bool> ServerExistsAsync(int serverId, CancellationToken ct);

    void Add(ServerRebootSchedule schedule);
    void Remove(ServerRebootSchedule schedule);

    /// <summary>Saves the schedule and the new order task together: the task gets its id, the schedule points at it.</summary>
    Task AddOrderAsync(ServerRebootSchedule schedule, ServerTask order, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

internal sealed class ServerRebootRepository(AppDbContext db) : IServerRebootRepository
{
    public Task<ServerRebootSchedule?> FindAsync(int serverId, CancellationToken ct) =>
        db.ServerRebootSchedules.FirstOrDefaultAsync(schedule => schedule.ServerId == serverId, ct);

    public async Task<ServerRebootScheduleRow?> GetRowAsync(int serverId, CancellationToken ct)
    {
        var rows = await Rows(db.ServerRebootSchedules.AsNoTracking().Where(schedule => schedule.ServerId == serverId))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public Task<List<ServerRebootScheduleRow>> GetRowsAsync(
        int organizationId, IReadOnlyCollection<int>? serverIds, CancellationToken ct)
    {
        var schedules = db.ServerRebootSchedules.AsNoTracking()
            .Where(schedule => schedule.Server.OrganizationId == organizationId);
        if (serverIds is not null)
        {
            schedules = schedules.Where(schedule => serverIds.Contains(schedule.ServerId));
        }

        return Rows(schedules).ToListAsync(ct);
    }

    public Task<List<MachineRebootRow>> GetMachineRebootsAsync(int organizationId, IReadOnlyCollection<int>? serverIds, CancellationToken ct)
    {
        var servers = db.Servers.AsNoTracking().Where(server => server.OrganizationId == organizationId && server.MachineRebootJson != null);
        if (serverIds is not null)
        {
            servers = servers.Where(server => serverIds.Contains(server.Id));
        }

        return servers
            .OrderBy(server => server.Name)
            .ThenBy(server => server.Id)
            .Select(server => new MachineRebootRow(server.Id, server.Name, server.MachineRebootJson!))
            .ToListAsync(ct);
    }

    public Task<List<MachineJobsRow>> GetMachineJobsAsync(int organizationId, IReadOnlyCollection<int>? serverIds, CancellationToken ct)
    {
        var servers = db.Servers.AsNoTracking().Where(server => server.OrganizationId == organizationId && server.MachineJobsJson != null);
        if (serverIds is not null)
        {
            servers = servers.Where(server => serverIds.Contains(server.Id));
        }

        return servers
            .OrderBy(server => server.Name)
            .ThenBy(server => server.Id)
            .Select(server => new MachineJobsRow(server.Id, server.Name, server.MachineJobsJson!))
            .ToListAsync(ct);
    }

    public Task<List<ServerRebootSchedule>> GetEnabledAsync(CancellationToken ct) =>
        db.ServerRebootSchedules.Where(schedule => schedule.Enabled).OrderBy(schedule => schedule.ServerId).ToListAsync(ct);

    public Task<string?> FindBusyTaskNameAsync(int serverId, int? exceptTaskId, CancellationToken ct) =>
        db.Tasks.AsNoTracking()
            .Where(task => task.ServerId == serverId
                && (task.Status == TaskExecutionStatus.Assigned || task.Status == TaskExecutionStatus.Running)
                && task.Operation != OperationKind.ServerReboot
                && (exceptTaskId == null || task.Id != exceptTaskId))
            .OrderBy(task => task.Id)
            .Select(task => task.Name)
            .FirstOrDefaultAsync(ct);

    public Task<RebootOrderTaskRow?> GetOrderTaskAsync(int taskId, CancellationToken ct) =>
        db.Tasks.AsNoTracking()
            .Where(task => task.Id == taskId)
            .Select(task => new RebootOrderTaskRow(task.Id, task.ServerId, task.Operation, task.Status, task.EnvironmentVariables))
            .FirstOrDefaultAsync(ct);

    public Task<bool> ServerExistsAsync(int serverId, CancellationToken ct) =>
        ServerTaskRepositoryOperations.ServerExistsAsync(db, serverId, ct);

    public void Add(ServerRebootSchedule schedule) => db.ServerRebootSchedules.Add(schedule);

    public void Remove(ServerRebootSchedule schedule) => db.ServerRebootSchedules.Remove(schedule);

    public async Task AddOrderAsync(ServerRebootSchedule schedule, ServerTask order, CancellationToken ct)
    {
        // Two saves: the task needs its id first. Should the second fail, the task points at no schedule and the
        // agent's confirmation refuses it (not the current order), so a lost write never reboots a host.
        await ServerTaskRepositoryOperations.AddTaskAsync(db, order, ct).ConfigureAwait(false);
        schedule.LastTaskId = order.Id;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // The last order task is read by its id (a left join): a schedule that never ordered has no task.
    private IQueryable<ServerRebootScheduleRow> Rows(IQueryable<ServerRebootSchedule> schedules) =>
        schedules
            .OrderBy(schedule => schedule.Server.Name)
            .ThenBy(schedule => schedule.ServerId)
            .Select(schedule => new ServerRebootScheduleRow(
                schedule,
                schedule.Server.Name,
                schedule.Server.OrganizationId,
                schedule.Server.AgentCapabilitiesJson,
                db.Tasks.Where(task => task.Id == schedule.LastTaskId).Select(task => (TaskExecutionStatus?)task.Status).FirstOrDefault(),
                db.Tasks.Where(task => task.Id == schedule.LastTaskId).Select(task => task.CompletedAt).FirstOrDefault(),
                db.Tasks.Where(task => task.Id == schedule.LastTaskId).Select(task => task.FailureReason).FirstOrDefault()));
}
