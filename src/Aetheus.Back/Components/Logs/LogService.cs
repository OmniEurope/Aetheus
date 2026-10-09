// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Logs;

public class LogService(ILogRepository repo, IHubContext<LogHub> logHub, ISecretMaskingService secretMasking, IEncryptionService encryption) : ILogService
{
    public async Task<List<TaskLogDto>> GetTaskLogsAsync(int taskId, int? maxLines = null, CancellationToken ct = default)
    {
        var boundedMaxLines = maxLines.HasValue ? Math.Clamp(maxLines.Value, 1, 20000) : (int?)null;
        var logs = await repo.GetTaskLogsAsync(taskId, boundedMaxLines, ct).ConfigureAwait(false);
        return MapLogs(logs, static log => log.Message);
    }

    public async Task<PaginatedResult<TaskLogDto>> GetTaskLogPageAsync(int taskId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var (items, total) = await repo.GetTaskLogPageAsync(taskId, page, pageSize, ct).ConfigureAwait(false);
        return new PaginatedResult<TaskLogDto>
        {
            Items = MapLogs(items, static log => log.Message),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<List<string>> GetTaskOutputVariableLinesAsync(int taskId, CancellationToken ct = default) =>
        repo.GetTaskOutputVariableLinesAsync(taskId, ct);

    public async Task<List<TaskLogDto>> GetTaskLogsUnmaskedAsync(int taskId, int? maxLines = null, CancellationToken ct = default)
    {
        var boundedMaxLines = maxLines.HasValue ? Math.Clamp(maxLines.Value, 1, 20000) : (int?)null;
        var logs = await repo.GetTaskLogsAsync(taskId, boundedMaxLines, ct).ConfigureAwait(false);
        return MapLogs(logs, log => string.IsNullOrEmpty(log.OriginalMessage)
            ? log.Message
            : encryption.DecryptValue(log.OriginalMessage));
    }

    private static List<TaskLogDto> MapLogs(
        IEnumerable<TaskLog> logs,
        Func<TaskLog, string> getMessage) =>
        logs.Select(log => new TaskLogDto
        {
            Id = log.Id,
            TaskId = log.TaskId,
            Level = log.Level,
            Message = getMessage(log),
            Timestamp = log.Timestamp
        }).ToList();

    public async Task AppendLogAsync(AppendLogRequest request, CancellationToken ct = default)
    {
        var pipelineRunId = await repo.GetPipelineRunIdForTaskAsync(request.TaskId, ct).ConfigureAwait(false);
        var maskedMessage = await secretMasking.MaskAsync(request.Message, pipelineRunId, ct).ConfigureAwait(false);
        var hasMasking = !string.Equals(request.Message, maskedMessage, StringComparison.Ordinal);

        var log = new TaskLog
        {
            TaskId = request.TaskId,
            Level = request.Level,
            Message = maskedMessage,
            // F-16: store the unmasked original encrypted at rest.
            OriginalMessage = hasMasking ? encryption.EncryptValue(request.Message) : null
        };
        await repo.AddLogAsync(log, ct).ConfigureAwait(false);

        await logHub.Clients.Group($"task-{request.TaskId}").SendAsync("LogReceived", new TaskLogDto
        {
            Id = log.Id,
            TaskId = log.TaskId,
            Level = log.Level,
            Message = maskedMessage,
            Timestamp = log.Timestamp
        }, ct).ConfigureAwait(false);
    }

    public async Task AppendLogsAsync(List<AppendLogRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0) return;

        // Resolve pipeline run IDs and mask messages
        var taskPipelineRunIds = new Dictionary<int, int?>();
        var maskedLogs = new List<TaskLog>(requests.Count);

        foreach (var request in requests)
        {
            if (!taskPipelineRunIds.TryGetValue(request.TaskId, out var pipelineRunId))
            {
                pipelineRunId = await repo.GetPipelineRunIdForTaskAsync(request.TaskId, ct).ConfigureAwait(false);
                taskPipelineRunIds[request.TaskId] = pipelineRunId;
            }

            var maskedMessage = await secretMasking.MaskAsync(request.Message, pipelineRunId, ct).ConfigureAwait(false);
            var hasMasking = !string.Equals(request.Message, maskedMessage, StringComparison.Ordinal);
            maskedLogs.Add(new TaskLog
            {
                TaskId = request.TaskId,
                Level = request.Level,
                Message = maskedMessage,
                // F-16: store the unmasked original encrypted at rest.
                OriginalMessage = hasMasking ? encryption.EncryptValue(request.Message) : null
            });
        }

        await repo.AddLogsAsync(maskedLogs, ct).ConfigureAwait(false);

        // F20: Group by TaskId and broadcast a single batched message per group
        // instead of one SignalR send per log line.
        foreach (var group in maskedLogs.GroupBy(l => l.TaskId))
        {
            var dtos = group.Select(log => new TaskLogDto
            {
                // FRONT-010: the persisted id travels with the line, so the run page can tell a streamed
                // line from the same line read again in a snapshot or a page of the full log.
                Id = log.Id,
                TaskId = log.TaskId,
                Level = log.Level,
                Message = log.Message,
                Timestamp = log.Timestamp
            }).ToList();

            await logHub.Clients.Group($"task-{group.Key}").SendAsync("LogsReceived", dtos, ct).ConfigureAwait(false);
        }
    }
}
