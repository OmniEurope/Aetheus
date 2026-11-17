// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>Recette R-8 (option B of 2026-11-07): the reboot schedule of a server, and the backend side of the agent's
/// confirmation of a reboot order. The caller's rights are checked by the controller (Read to see, Admin to change).</summary>
public interface IServerRebootScheduleService
{
    Task<ServerRebootScheduleDto?> GetAsync(int serverId, bool canManage, CancellationToken ct);
    Task<ServerRebootScheduleDto> SaveAsync(int serverId, SaveServerRebootScheduleRequest request, string actor, CancellationToken ct);
    Task DeleteAsync(int serverId, CancellationToken ct);

    /// <summary>Whether the agent of <paramref name="agentServerId"/> may execute the reboot order <paramref name="taskId"/>
    /// now. Every answer is audited.</summary>
    Task<RebootOrderConfirmationDto> ConfirmOrderAsync(int agentServerId, int taskId, CancellationToken ct);
}

internal sealed class ServerRebootScheduleService(
    IServerRebootRepository repository, IAuditService audit, TimeProvider clock) : IServerRebootScheduleService
{
    internal const string AuditEntity = "ServerReboot";

    public async Task<ServerRebootScheduleDto?> GetAsync(int serverId, bool canManage, CancellationToken ct)
    {
        var row = await repository.GetRowAsync(serverId, ct).ConfigureAwait(false);
        return row is null ? null : ToDto(row, canManage, clock.GetUtcNow().UtcDateTime);
    }

    public async Task<ServerRebootScheduleDto> SaveAsync(
        int serverId, SaveServerRebootScheduleRequest request, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ServerRebootOccurrences.FindZone(request.TimeZoneId) is null)
            throw new BadRequestException($"Unknown time zone: {request.TimeZoneId}.");
        if (!await repository.ServerExistsAsync(serverId, ct).ConfigureAwait(false))
            throw new NotFoundException($"Server {serverId} not found.");

        var now = clock.GetUtcNow().UtcDateTime;
        var schedule = await repository.FindAsync(serverId, ct).ConfigureAwait(false);
        if (schedule is null)
        {
            schedule = new ServerRebootSchedule { ServerId = serverId };
            repository.Add(schedule);
        }

        schedule.Enabled = request.Enabled;
        schedule.Hour = request.Hour;
        schedule.Minute = request.Minute;
        schedule.TimeZoneId = request.TimeZoneId;
        schedule.Version++;
        schedule.UpdatedAtUtc = now;
        schedule.UpdatedBy = actor;
        // A postponed occurrence belongs to the old schedule: it is dropped with it, never fired for the new one.
        if (schedule.LastOutcome == RebootScheduleOutcome.Postponed)
        {
            schedule.LastOutcome = RebootScheduleOutcome.None;
            schedule.OutcomeDetail = null;
        }

        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("ServerRebootScheduleSaved", AuditEntity, serverId, Describe(schedule), ct).ConfigureAwait(false);
        var row = await repository.GetRowAsync(serverId, ct).ConfigureAwait(false);
        return ToDto(row!, canManage: true, now);
    }

    public async Task DeleteAsync(int serverId, CancellationToken ct)
    {
        var schedule = await repository.FindAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} has no reboot schedule.");
        var description = Describe(schedule);
        repository.Remove(schedule);
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("ServerRebootScheduleDeleted", AuditEntity, serverId, description, ct).ConfigureAwait(false);
    }

    public async Task<RebootOrderConfirmationDto> ConfirmOrderAsync(int agentServerId, int taskId, CancellationToken ct)
    {
        var refusal = await CheckOrderAsync(agentServerId, taskId, ct).ConfigureAwait(false);
        var details = string.Create(CultureInfo.InvariantCulture, $"task {taskId}: {(refusal == RebootOrderRefusal.None ? "confirmed" : refusal.ToString())}");
        await audit.LogAsync(
            refusal == RebootOrderRefusal.None ? "ServerRebootConfirmed" : "ServerRebootRefused",
            AuditEntity, agentServerId, details, ct).ConfigureAwait(false);
        return new RebootOrderConfirmationDto { Allowed = refusal == RebootOrderRefusal.None, Refusal = refusal };
    }

    private async Task<RebootOrderRefusal> CheckOrderAsync(int agentServerId, int taskId, CancellationToken ct)
    {
        var task = await repository.GetOrderTaskAsync(taskId, ct).ConfigureAwait(false);
        // Another server's task reads exactly like no task: an agent learns nothing about the rest of the fleet.
        if (task is null || task.ServerId != agentServerId || task.Operation != OperationKind.ServerReboot)
            return RebootOrderRefusal.NotARebootOrder;

        var schedule = await repository.FindAsync(agentServerId, ct).ConfigureAwait(false);
        if (schedule is null)
            return RebootOrderRefusal.ScheduleRemoved;
        if (!schedule.Enabled)
            return RebootOrderRefusal.ScheduleDisabled;

        var order = ReadOrder(task.EnvironmentVariables);
        if (order.Version != schedule.Version)
            return RebootOrderRefusal.ScheduleChanged;
        if (schedule.LastTaskId != taskId)
            return RebootOrderRefusal.NotTheCurrentOrder;
        if (order.NotAfterUtc is not { } notAfter || clock.GetUtcNow().UtcDateTime > notAfter)
            return RebootOrderRefusal.Expired;

        var busy = await repository.FindBusyTaskNameAsync(agentServerId, taskId, ct).ConfigureAwait(false);
        return busy is null ? RebootOrderRefusal.None : RebootOrderRefusal.ServerBusy;
    }

    private static (int? Version, DateTime? NotAfterUtc) ReadOrder(string environmentJson)
    {
        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(environmentJson) ?? [];
        int? version = env.TryGetValue(ServerRebootOrder.ScheduleVersionEnvVar, out var v)
            && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        DateTime? notAfter = env.TryGetValue(ServerRebootOrder.NotAfterEnvVar, out var n)
            && DateTime.TryParse(n, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at : null;
        return (version, notAfter);
    }

    internal static string Describe(ServerRebootSchedule schedule) => string.Create(CultureInfo.InvariantCulture,
        $"daily {schedule.Hour:00}:{schedule.Minute:00} {schedule.TimeZoneId}, {(schedule.Enabled ? "enabled" : "disabled")}, version {schedule.Version}");

    internal static ServerRebootScheduleDto ToDto(ServerRebootScheduleRow row, bool canManage, DateTime nowUtc)
    {
        var schedule = row.Schedule;
        return new ServerRebootScheduleDto
        {
            ServerId = schedule.ServerId,
            ServerName = row.ServerName,
            Enabled = schedule.Enabled,
            Hour = schedule.Hour,
            Minute = schedule.Minute,
            TimeZoneId = schedule.TimeZoneId,
            Version = schedule.Version,
            UpdatedAtUtc = schedule.UpdatedAtUtc,
            UpdatedBy = schedule.UpdatedBy,
            NextRunUtc = schedule.Enabled ? ServerRebootOccurrences.Next(schedule, nowUtc) : null,
            LastOutcome = schedule.LastOutcome,
            LastOutcomeAtUtc = schedule.LastOutcomeAtUtc,
            LastOccurrenceUtc = schedule.LastOccurrenceUtc,
            SkipReason = schedule.SkipReason,
            OutcomeDetail = schedule.OutcomeDetail,
            LastTaskId = schedule.LastTaskId,
            LastTaskStatus = row.LastTaskStatus,
            LastTaskCompletedAtUtc = row.LastTaskCompletedAtUtc,
            LastTaskFailureReason = row.LastTaskFailureReason,
            CanManage = canManage,
            AgentCanReboot = AgentCapabilities.IsDeclared(row.AgentCapabilitiesJson, AgentCapabilities.HostReboot)
        };
    }
}
