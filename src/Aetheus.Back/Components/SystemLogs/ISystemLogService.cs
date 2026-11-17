// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.SystemLogs;

public interface ISystemLogService
{
    Task<List<SystemLogFileDto>> GetLogFilesAsync(CancellationToken ct = default);

    /// <param name="exclusionRules">The organization's rules (R-28) that split the entries into the two tabs of
    /// the page; null shows every entry, whatever the rules.</param>
    /// <param name="onlyExcluded">With rules, false returns the entries no enabled rule matches (the first tab),
    /// true the entries one matches (the second tab): together they are the whole log.</param>
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
        IReadOnlyList<GridFilter>? filters = null,
        IReadOnlyList<SystemLogExclusionRuleDto>? exclusionRules = null,
        bool onlyExcluded = false);

    Task<Stream> DownloadLogFileAsync(string fileName, CancellationToken ct = default);

    Task<int> PurgeOldLogsAsync(int retentionDays, CancellationToken ct = default);
}
