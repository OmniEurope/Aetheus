// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.SystemLogs;

public interface ISystemLogService
{
    Task<List<SystemLogFileDto>> GetLogFilesAsync(CancellationToken ct = default);

    Task<PaginatedResult<SystemLogEntryDto>> GetLogEntriesAsync(
        string? fileName,
        string? level,
        string? search,
        DateTime? dateFrom,
        DateTime? dateTo,
        int page,
        int pageSize,
        CancellationToken ct = default,
        string? sortBy = null,
        bool sortDescending = true,
        IReadOnlyList<GridFilter>? filters = null);

    Task<Stream> DownloadLogFileAsync(string fileName, CancellationToken ct = default);

    Task<int> PurgeOldLogsAsync(int retentionDays, CancellationToken ct = default);
}
