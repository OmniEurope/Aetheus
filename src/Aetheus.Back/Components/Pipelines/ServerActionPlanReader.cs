// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.VulnerabilityWatch;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The scheduled actions of an organization over a UTC window, for the planner (recette R-8): the servers' rkhunter scans
/// (the cron expression of the server's section, five fields, fired by <c>RkhunterSchedulerService</c>), the enabled AI
/// tasks (six fields, <c>AiTaskSchedulerService</c>) and the enabled backup policies (five fields,
/// <c>BackupSchedulerService</c>). Each source is listed only when the caller may read it and only inside the requested
/// organization; it is movable only with the right its own write endpoint asks for (<see cref="ServerActionScope"/>).
/// The restore-check cadence of a policy (five fields, <c>BackupSchedulerService</c>) follows the backup's project and is read only.
/// The CVE watch (<c>VulnerabilityFeedService</c>, one pass a day at a configured local time and not a cron) is listed for site
/// administrators only, and movable by them: its times are the daily occurrences of the schedule it really waits for (the time an
/// administrator moved it to, else the configured one, see <see cref="IVulnerabilityPassScheduleSource"/>). Nothing is written here.
/// </summary>
internal sealed class ServerActionPlanReader(
    IServerActionScheduleRepository repository, TimeProvider clock, IOptions<VulnerabilityWatchOptions> watchOptions,
    IServerRebootPlanSource? reboots = null, IVulnerabilityPassScheduleSource? passSource = null)
{
    public async Task<List<ScheduledServerActionDto>> ReadAsync(
        int organizationId, ServerActionScope scope, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var window = new Window(fromUtc, toUtc, now);
        List<ScheduledServerActionDto> actions = [];
        actions.AddRange(await ReadScansAsync(organizationId, scope, window, ct).ConfigureAwait(false));
        actions.AddRange(await ReadAiTasksAsync(organizationId, scope, window, ct).ConfigureAwait(false));
        actions.AddRange(await ReadBackupsAsync(organizationId, scope, window, ct).ConfigureAwait(false));
        actions.AddRange(await ReadRestoreChecksAsync(organizationId, scope, window, ct).ConfigureAwait(false));
        actions.AddRange(await ReadCveWatchAsync(scope, window, ct).ConfigureAwait(false));
        if (reboots is not null && !scope.SeesNoScan)
        {
            // The reboots Aetheus schedules (R-8, option B), read only here and changed in the server page, and the ones the
            // machine decides itself (R-11, option A), read by its agent: same right, the readable servers.
            actions.AddRange(await reboots.ReadPlanAsync(
                organizationId, scope.ReadableServerIds, scope.AdministeredServerIds, fromUtc, toUtc, ct).ConfigureAwait(false));
        }

        return actions;
    }

    private async Task<List<ScheduledServerActionDto>> ReadScansAsync(int organizationId, ServerActionScope scope, Window window, CancellationToken ct)
    {
        if (scope.SeesNoScan)
        {
            return [];
        }

        var rows = await repository.GetRkhunterSchedulesAsync(organizationId, scope.ReadableServerIds, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var averages = await repository.GetRkhunterAverageMinutesAsync([.. rows.Select(row => row.ServerId)], ct).ConfigureAwait(false);
        return [.. rows.Select(row => Build(
            window, ServerActionKind.RkhunterScan, row.ServerId, row.CronExpression, averages, scope.CanMove(row.ServerId)) with
        {
            ServerId = row.ServerId,
            ServerName = row.ServerName
        })];
    }

    private async Task<List<ScheduledServerActionDto>> ReadAiTasksAsync(int organizationId, ServerActionScope scope, Window window, CancellationToken ct)
    {
        if (scope.SeesNoAiTask)
        {
            return [];
        }

        var rows = await repository
            .GetAiTaskSchedulesAsync(organizationId, scope.ReadableProjectIds, scope.ReadableServerIds, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var averages = await repository.GetAiTaskAverageMinutesAsync([.. rows.Select(row => row.TaskId)], ct).ConfigureAwait(false);
        return [.. rows.Select(row => Build(
            window, ServerActionKind.AiTask, row.TaskId, row.Schedule, averages, scope.CanMoveAiTask(row.ProjectId, row.ServerId)) with
        {
            SourceName = row.TaskName,
            ServerId = row.ServerId ?? 0,
            ServerName = row.ServerName ?? string.Empty,
            ProjectId = row.ProjectId,
            ProjectName = row.ProjectName
        })];
    }

    private async Task<List<ScheduledServerActionDto>> ReadBackupsAsync(int organizationId, ServerActionScope scope, Window window, CancellationToken ct)
    {
        if (scope.SeesNoBackup)
        {
            return [];
        }

        var rows = await repository.GetBackupSchedulesAsync(organizationId, scope.ReadableProjectIds, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var averages = await repository.GetBackupAverageMinutesAsync([.. rows.Select(row => row.PolicyId)], ct).ConfigureAwait(false);
        return [.. rows.Select(row => Build(
            window, ServerActionKind.AppBackup, row.PolicyId, row.ScheduleCron, averages, scope.CanMoveBackup(row.ProjectId)) with
        {
            SourceName = row.PolicyName,
            ServerId = row.ServerId,
            ServerName = row.ServerName,
            ProjectId = row.ProjectId,
            ProjectName = row.ProjectName
        })];
    }

    private async Task<List<ScheduledServerActionDto>> ReadRestoreChecksAsync(int organizationId, ServerActionScope scope, Window window, CancellationToken ct)
    {
        if (scope.SeesNoRestoreCheck)
        {
            return [];
        }

        var rows = await repository.GetRestoreCheckSchedulesAsync(organizationId, scope.ReadableProjectIds, ct).ConfigureAwait(false);
        // No duration is measured for a restore-check: it is the planned one, said as such (IsPlanned), and never movable here.
        return [.. rows.Select(row => Build(
            window, ServerActionKind.RestoreCheck, row.PolicyId, row.RestoreCheckCron, [], movable: false) with
        {
            SourceName = row.PolicyName,
            ServerId = row.ServerId,
            ServerName = row.ServerName,
            ProjectId = row.ProjectId,
            ProjectName = row.ProjectName
        })];
    }

    /// <summary>The daily pass of the vulnerability feed service (recette R-3): one occurrence per local day at the time
    /// and in the zone the service really waits for (<see cref="VulnerabilityWatchOptions.PassSchedule"/>), computed from
    /// the configuration and never from the last pass. Its length is what the latest successful passes of the feeds took,
    /// offline imports left out.</summary>
    private async Task<List<ScheduledServerActionDto>> ReadCveWatchAsync(ServerActionScope scope, Window window, CancellationToken ct)
    {
        var options = watchOptions.Value;
        if (!scope.SeesCveWatch || !options.Enabled)
        {
            return [];
        }

        var schedule = passSource is null ? options.PassSchedule : await passSource.GetAsync(ct).ConfigureAwait(false);
        var durationMs = await repository.GetCveWatchPassDurationMsAsync(ct).ConfigureAwait(false);
        var isPlanned = durationMs <= 0;
        var durationMinutes = isPlanned
            ? ServerActionDefaults.CveWatchPlannedMinutes
            : Math.Max(1, (int)Math.Ceiling(durationMs / 60000d));
        List<ScheduleOccurrenceDto> occurrences =
        [
            .. schedule.Between(window.FromUtc, window.ToUtc).Take(ScheduleOccurrenceExpander.MaxOccurrences)
                .Select(start => new ScheduleOccurrenceDto { StartUtc = start, EndUtc = start.AddMinutes(durationMinutes) })
        ];
        return
        [
            new ScheduledServerActionDto
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"{IdPrefix(ServerActionKind.CveWatch)}:0"),
                Kind = ServerActionKind.CveWatch,
                SourceId = 0,
                DailyTime = schedule.Time.ToString("HH:mm", CultureInfo.InvariantCulture),
                DailyTimeZoneId = schedule.TimeZone.Id,
                DurationMinutes = durationMinutes,
                IsPlanned = isPlanned,
                NextRunUtc = schedule.NextAfter(window.NowUtc),
                Occurrences = occurrences,
                Movable = scope.SeesCveWatch
            }
        ];
    }

    private static ScheduledServerActionDto Build(
        Window window, ServerActionKind kind, int sourceId, string storedCron, Dictionary<int, int> averages, bool movable)
    {
        var cron = ServerActionDefaults.ToEngineFields(kind, storedCron);
        var isPlanned = !averages.TryGetValue(sourceId, out var average);
        var durationMinutes = isPlanned ? ServerActionDefaults.PlannedMinutes(kind) : average;
        var expansion = ScheduleOccurrenceExpander.ExpandBounded(cron, window.FromUtc, window.ToUtc, durationMinutes);
        var frequency = ScheduleFrequencyCodec.Decode(cron);
        return new ScheduledServerActionDto
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"{IdPrefix(kind)}:{sourceId}"),
            Kind = kind,
            SourceId = sourceId,
            CronExpression = storedCron,
            Frequency = frequency.Frequency,
            Day = frequency.Day,
            Hour = frequency.Hour,
            Minute = frequency.Minute,
            DurationMinutes = durationMinutes,
            IsPlanned = isPlanned,
            NextRunUtc = ScheduleOccurrenceExpander.NextOccurrence(cron, window.NowUtc),
            Occurrences = expansion.Occurrences,
            OccurrencesTruncated = expansion.IsTruncated,
            Movable = movable
        };
    }

    private static string IdPrefix(ServerActionKind kind) => kind switch
    {
        ServerActionKind.RkhunterScan => "rkhunter",
        ServerActionKind.AiTask => "ai",
        ServerActionKind.AppBackup => "backup",
        ServerActionKind.RestoreCheck => "restore",
        ServerActionKind.CveWatch => "cve",
        ServerActionKind.ServerReboot => "reboot",
        ServerActionKind.MachineReboot => "machine-reboot",
        ServerActionKind.MachineJob => "machine-job",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Every kind of action has its own id prefix.")
    };

    private readonly record struct Window(DateTime FromUtc, DateTime ToUtc, DateTime NowUtc);
}
