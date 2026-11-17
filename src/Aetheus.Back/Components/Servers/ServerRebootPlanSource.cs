// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Servers;

/// <summary>Recette R-8: the enabled reboot schedules of an organization as planner rows, beside the pipelines. Read only
/// in the planner (a reboot is changed in the server page); the times keep the schedule's time zone, the occurrences are UTC.
/// Recette R-11: the reboots a machine decides by itself, as its agent reported them, follow as read-only rows of their own
/// sort (<see cref="ServerActionKind.MachineReboot"/>). Recette R-27: the jobs a machine schedules by itself (cron, whitelisted
/// systemd timers) follow the same way (<see cref="ServerActionKind.MachineJob"/>).</summary>
public interface IServerRebootPlanSource
{
    /// <param name="readableServerIds">The servers the caller may read; null for every server.</param>
    /// <param name="administeredServerIds">The servers the caller administers; null for every server.</param>
    Task<List<ScheduledServerActionDto>> ReadPlanAsync(
        int organizationId, IReadOnlyCollection<int>? readableServerIds, IReadOnlyCollection<int>? administeredServerIds,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

internal sealed class ServerRebootPlanSource(IServerRebootRepository repository, TimeProvider clock) : IServerRebootPlanSource
{
    public async Task<List<ScheduledServerActionDto>> ReadPlanAsync(
        int organizationId, IReadOnlyCollection<int>? readableServerIds, IReadOnlyCollection<int>? administeredServerIds,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        if (readableServerIds is { Count: 0 })
        {
            return [];
        }

        var rows = await repository.GetRowsAsync(organizationId, readableServerIds, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        List<ScheduledServerActionDto> actions = [.. rows.Where(row => row.Schedule.Enabled).Select(row => Build(row, administeredServerIds, fromUtc, toUtc, now))];
        // R-11: the reboots the machine decides itself, read by its agent, listed with the same right as the reboots Aetheus orders.
        var machine = await repository.GetMachineRebootsAsync(organizationId, readableServerIds, ct).ConfigureAwait(false);
        actions.AddRange(machine.SelectMany(row => MachineRebootPlan.Build(row, fromUtc, toUtc, now)));
        // R-27: the jobs the machine schedules itself (crontabs, whitelisted timers), same pipeline and same right.
        var jobs = await repository.GetMachineJobsAsync(organizationId, readableServerIds, ct).ConfigureAwait(false);
        actions.AddRange(jobs.SelectMany(row => MachineJobPlan.Build(row, fromUtc, toUtc, now)));
        return actions;
    }

    private static ScheduledServerActionDto Build(
        ServerRebootScheduleRow row, IReadOnlyCollection<int>? administeredServerIds, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        var schedule = row.Schedule;
        var canManage = administeredServerIds is null || administeredServerIds.Contains(schedule.ServerId);
        var dto = ServerRebootScheduleService.ToDto(row, canManage, now);
        var occurrences = ServerRebootOccurrences.Between(schedule, fromUtc.AddTicks(-1), toUtc)
            .Where(start => start < toUtc)
            .Select(start => new ScheduleOccurrenceDto { StartUtc = start, EndUtc = start.AddMinutes(ServerRebootOrder.PlannedMinutes) })
            .ToList();
        var nextUtc = dto.NextRunUtc;
        return new ScheduledServerActionDto
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"reboot:{schedule.ServerId}"),
            Kind = ServerActionKind.ServerReboot,
            SourceId = schedule.ServerId,
            ServerId = schedule.ServerId,
            ServerName = row.ServerName,
            // The daily time in the schedule's own zone; the UTC hour below is the one of the next occurrence, which a
            // daylight-saving change may shift.
            CronExpression = ServerRebootOccurrences.ToCronExpression(schedule.Hour, schedule.Minute),
            Frequency = ScheduleFrequency.EveryEvening,
            Hour = nextUtc?.Hour,
            Minute = nextUtc?.Minute,
            DurationMinutes = ServerRebootOrder.PlannedMinutes,
            IsPlanned = true,
            NextRunUtc = nextUtc,
            Occurrences = occurrences,
            Movable = false,
            Reboot = dto
        };
    }
}
