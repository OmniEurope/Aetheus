// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Rkhunter;

public class RkhunterService(IRkhunterRepository repo, IAuditService audit, ITaskService taskService) : IRkhunterService
{
    private Task QueueTaskAsync(ServerTask task, CancellationToken ct = default)
        => TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct);

    public async Task<RkhunterDataDto> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null)
            return new RkhunterDataDto();

        return new RkhunterDataDto
        {
            IsInstalled = true,
            Version = state.Version,
            DatabaseVersion = state.DatabaseVersion,
            LastScanTime = state.LastScanTime,
            LastScanStatus = state.LastScanStatus,
            WarningCount = state.WarningCount,
            DatabaseLastUpdated = state.DatabaseLastUpdated,
            ScanScheduleCron = state.ScanScheduleCron
        };
    }

    public async Task ExecuteActionAsync(int serverId, RkhunterActionRequest request, CancellationToken ct = default)
    {
        // Items #10.2/#10.3: switched from a shell command (rkhunter as the agent user - would
        // silently fail without root) to a typed operation that invokes sudo via the
        // `install-agent-linux.sh --enable-rkhunter-manage` allow-list. Argv is exact, no
        // metachars, no chance of GTFOBins escalation.
        var op = request.Action switch
        {
            RkhunterAction.RunScan => OperationKind.RkhunterScan,
            RkhunterAction.UpdateDatabase => OperationKind.RkhunterUpdate,
            RkhunterAction.UpdateProperties => OperationKind.RkhunterPropupd,
            _ => throw new BadRequestException($"Unknown RKHunter action: {request.Action}")
        };
        var timeout = request.Action == RkhunterAction.RunScan ? 300 : 60;

        var task = ServerTaskFactory.Operation(serverId,
            $"RKHunter - {request.Action}", op, target: "-", timeoutSeconds: timeout);
        await QueueTaskAsync(task, ct).ConfigureAwait(false);

        await audit.LogAsync($"Rkhunter{request.Action}", "Rkhunter", serverId, request.Action.ToString(), ct).ConfigureAwait(false);
    }

    public async Task SetupAsync(int serverId, RkhunterSetupRequest request, CancellationToken ct = default)
    {
        // R-119: a typed operation (apt install through the package grant, then the root-owned aetheus-rkhunter
        // helper); its kind makes the agent collect its RKHunter inventory again at once. The warning address is the
        // only variable: checked here, by the agent and by the helper, and never part of a command line.
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(request.MailOnWarning))
        {
            if (!RkhunterCommandHelper.IsValidEmail(request.MailOnWarning))
                throw new BadRequestException("Mail on warning must be a valid email address.");
            env[RkhunterSetupEnv.MailOnWarning] = request.MailOnWarning;
        }

        // An apt install, a data update and a baseline of every system file: several minutes on a small host.
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, "RKHunter - setup", OperationKind.RkhunterSetup,
            target: "-", env, timeoutSeconds: 900), ct).ConfigureAwait(false);

        await audit.LogAsync("RkhunterSetup", "Rkhunter", serverId, request.MailOnWarning ?? "no-mail", ct).ConfigureAwait(false);
    }

    public async Task GetLogsAsync(int serverId, RkhunterLogRequest request, CancellationToken ct = default)
    {
        var lines = Math.Clamp(request.Lines, 1, 5000);
        var command = RkhunterCommandHelper.BuildGetLogsCommand(lines);

        await QueueTaskAsync(ServerTaskFactory.Shell(serverId, "RKHunter - logs", command, 15), ct).ConfigureAwait(false);
    }

    public async Task<List<RkhunterWarningDto>> GetWarningsAsync(int serverId, bool includeArchived = false, CancellationToken ct = default)
    {
        var warnings = await repo.GetWarningsAsync(serverId, includeArchived, ct).ConfigureAwait(false);
        return warnings.Select(w => new RkhunterWarningDto
        {
            Id = w.Id,
            Category = w.Category,
            Detail = w.Detail,
            Severity = w.Severity,
            FoundAt = w.FoundAt,
            IsArchived = w.IsArchived
        }).ToList();
    }

    public async Task<List<RkhunterScanResultDto>> GetScanHistoryAsync(int serverId, int limit = 50, CancellationToken ct = default)
    {
        var results = await repo.GetScanHistoryAsync(serverId, PaginationDefaults.Clamp(limit), ct).ConfigureAwait(false);
        return results.Select(r => new RkhunterScanResultDto
        {
            Id = r.Id,
            ScanTime = r.ScanTime,
            Status = r.Status,
            WarningCount = r.WarningCount,
            Summary = r.Summary
        }).ToList();
    }

    public async Task SetScheduleAsync(int serverId, RkhunterScheduleRequest request, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.CronExpression))
            Cronos.CronExpression.Parse(request.CronExpression);

        await repo.UpdateScanScheduleAsync(serverId, request.CronExpression?.Trim(), ct).ConfigureAwait(false);
        await audit.LogAsync("RkhunterSchedule", "Rkhunter", serverId, request.CronExpression ?? "disabled", ct).ConfigureAwait(false);
    }
}
