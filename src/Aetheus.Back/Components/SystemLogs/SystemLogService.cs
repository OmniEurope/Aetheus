// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Diagnostics.Logs;

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>
/// The system logs API over the OmniEurope.Diagnostics journal: the package lists, reads, opens and purges the log
/// files; this service only maps the entries to the page's contract, keeps the organization's exclusion rules
/// (database, per organization) and applies the grid's header filters and sort before paging.
/// </summary>
public sealed class SystemLogService(
    ILogJournal journal, ILogger<SystemLogService> logger, TimeProvider timeProvider) : ISystemLogService
{
    // Reading a truncated log is said once in a while, never on every request: the warning is itself a log line,
    // which would wake the live page, which would read again.
    private static readonly TimeSpan TruncationWarningSpacing = TimeSpan.FromMinutes(10);
    private static long _lastTruncationWarningTicks;

    public async Task<List<SystemLogFileDto>> GetLogFilesAsync(CancellationToken ct = default)
    {
        // The package does its file work on the calling thread: a request thread never waits on the disk.
        var files = await Task.Run(() => journal.ListFilesAsync(ct), ct).ConfigureAwait(false);
        return [.. files.Select(file => new SystemLogFileDto(file.Name, file.Length, file.LastWriteUtc.UtcDateTime))];
    }

    public async Task<PaginatedResult<SystemLogEntryDto>> GetLogEntriesAsync(
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
        bool onlyExcluded = false)
    {
        var wanted = ParseLevel(level);
        if (!string.IsNullOrWhiteSpace(level) && wanted is null)
            return Empty(page, pageSize);

        var query = new LogSearch
        {
            File = string.IsNullOrWhiteSpace(fileName) ? null : fileName,
            MinLevel = wanted,
            From = AsUtc(dateFrom),
            To = AsUtc(dateTo),
            Text = string.IsNullOrWhiteSpace(search) ? null : search
        };
        var result = await Task.Run(() => journal.SearchAsync(query, ct), ct).ConfigureAwait(false);
        if (result.Truncated)
            WarnTruncated();

        // The page's level filter is one level, not "this level and above": the package keeps the level and above.
        var entries = result.Entries.Select(row => ToDto(row.Entry))
            .Where(entry => wanted is not { } only || entry.Level == only.ToString())
            .Where(entry => exclusionRules is null || SystemLogExclusion.MatchesAny(entry, exclusionRules) == onlyExcluded);

        // The grid's header filters and sort apply to every entry kept above, newest first when the grid names no sort.
        var filtered = SystemLogQuery.Columns.ApplyFilters(entries.AsQueryable(), filters);
        var ordered = SystemLogQuery.Columns.ApplySorts(filtered, SystemLogQuery.SortsOf(sortBy, sortDescending))
            ?? filtered.OrderByDescending(entry => entry.Timestamp);
        var matching = ordered.ToList();
        var items = matching.Skip((page - 1) * pageSize).Take(pageSize)
            .Select((entry, index) => entry with { Id = (page - 1) * pageSize + index + 1 })
            .ToList();
        return new PaginatedResult<SystemLogEntryDto>
        {
            Items = items,
            TotalCount = matching.Count,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<Stream> DownloadLogFileAsync(string fileName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new BadRequestException("Invalid file name.");
        return await Task.Run(() => journal.OpenFileAsync(fileName, ct), ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Log file '{fileName}' not found.");
    }

    public async Task<int> PurgeOldLogsAsync(int retentionDays, CancellationToken ct = default)
    {
        var purged = await Task.Run(() => journal.PurgeAsync(retentionDays, ct), ct).ConfigureAwait(false);
        if (purged.Failed > 0)
            logger.LogWarning("Failed to purge {Failed} log file(s); {Deleted} deleted", purged.Failed, purged.Deleted);
        return purged.Deleted;
    }

    private void WarnTruncated()
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastTruncationWarningTicks);
        if (now - last < TruncationWarningSpacing.Ticks
            || Interlocked.CompareExchange(ref _lastTruncationWarningTicks, now, last) != last)
            return;
        logger.LogWarning(
            "System log query hit the read cap (OmniLogs:MaxBytesRead); older entries were left out. Narrow the date range or file filter.");
    }

    private static LogSeverity? ParseLevel(string? level) =>
        Enum.TryParse<LogSeverity>(level, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static DateTimeOffset? AsUtc(DateTime? value) =>
        value is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;

    private static SystemLogEntryDto ToDto(LogEntry entry) => new(
        Id: 0,
        Timestamp: entry.Timestamp.UtcDateTime,
        Level: entry.Level.ToString(),
        Category: entry.Category ?? string.Empty,
        Message: entry.Message,
        Exception: entry.Exception,
        CorrelationId: entry.CorrelationId);

    private static PaginatedResult<SystemLogEntryDto> Empty(int page, int pageSize) =>
        new() { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };
}
