// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Logs;

public interface ILogService
{
    Task<List<TaskLogDto>> GetTaskLogsAsync(int taskId, int? maxLines = null, CancellationToken ct = default);
    /// <summary>
    /// Recette R2-104: one page of a task's masked log from its first line, so the run page can show a
    /// step's complete log on demand while its live view keeps only a bounded tail.
    /// </summary>
    Task<PaginatedResult<TaskLogDto>> GetTaskLogPageAsync(int taskId, int page, int pageSize, CancellationToken ct = default);
    Task<List<string>> GetTaskOutputVariableLinesAsync(int taskId, CancellationToken ct = default);
    Task<List<TaskLogDto>> GetTaskLogsUnmaskedAsync(int taskId, int? maxLines = null, CancellationToken ct = default);
    Task AppendLogAsync(AppendLogRequest request, CancellationToken ct = default);
    Task AppendLogsAsync(List<AppendLogRequest> requests, CancellationToken ct = default);
}
