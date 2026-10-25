// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Back.Components.SystemLogs;

public class SystemLogService : ISystemLogService
{
    private readonly string _logDirectory;
    private readonly ILogger<SystemLogService> _logger;
    private readonly TimeProvider _timeProvider;
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;
    // Hard cap on entries materialized in memory across all target files before paging (audit Faible:
    // bound the working set of a query over a large log directory). Truncation is logged, never silent.
    private const int MaxAggregatedEntries = 200_000;

    public SystemLogService(IConfiguration configuration, ILogger<SystemLogService> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _logDirectory = configuration["Logging:FileLog:Directory"]
            ?? Path.Combine(AppContext.BaseDirectory, "logs");
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<List<SystemLogFileDto>> GetLogFilesAsync(CancellationToken ct = default)
    {
        var result = new List<SystemLogFileDto>();
        if (!Directory.Exists(_logDirectory)) return result;

        var files = await Task.Run(() => Directory.GetFiles(_logDirectory, "*.log")
            .Concat(Directory.GetFiles(_logDirectory, "*.json")), ct).ConfigureAwait(false);

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(filePath);
            result.Add(new SystemLogFileDto(info.Name, info.Length, info.LastWriteTimeUtc));
        }

        return [.. result.OrderByDescending(f => f.LastModified)];
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
        IReadOnlyList<GridFilter>? filters = null)
    {
        if (!Directory.Exists(_logDirectory))
            return new PaginatedResult<SystemLogEntryDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };

        var files = GetTargetFiles(fileName);
        var filtered = new List<SystemLogEntryDto>();
        var truncated = false;
        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(filePath);
            if (info.Length > MaxFileSizeBytes)
            {
                _logger.LogWarning("Skipping oversized log file {FilePath} ({SizeMB:F1} Mo)",
                    filePath, info.Length / (1024.0 * 1024.0));
                continue;
            }
            await foreach (var entry in StreamLogFileAsync(filePath, ct).ConfigureAwait(false))
            {
                if (!MatchesFilter(entry, level, search, dateFrom, dateTo)) continue;
                filtered.Add(entry);
                if (filtered.Count >= MaxAggregatedEntries) { truncated = true; break; }
            }
            if (truncated) break;
        }

        if (truncated)
            _logger.LogWarning(
                "System log query hit the {Cap}-entry in-memory cap; results truncated. Narrow the date range or file filter.",
                MaxAggregatedEntries);

        // The grid's header filters and sort apply to every entry kept above, newest first when the
        // grid names no sort.
        var query = SystemLogQuery.Columns.ApplyFilters(filtered.AsQueryable(), filters);
        var ordered = SystemLogQuery.Columns.ApplySorts(query, SystemLogQuery.SortsOf(sortBy, sortDescending))
            ?? query.OrderByDescending(entry => entry.Timestamp);
        var matching = ordered.ToList();
        var totalCount = matching.Count;
        var items = matching.Skip((page - 1) * pageSize).Take(pageSize)
            .Select((e, i) => e with { Id = (page - 1) * pageSize + i + 1 })
            .ToList();
        return new PaginatedResult<SystemLogEntryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public Task<Stream> DownloadLogFileAsync(string fileName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var sanitized = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized != fileName)
            throw new BadRequestException("Invalid file name.");
        var filePath = Path.Combine(_logDirectory, sanitized);
        if (!File.Exists(filePath))
            throw new NotFoundException($"Log file '{sanitized}' not found.");
        ct.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<int> PurgeOldLogsAsync(int retentionDays, CancellationToken ct = default)
    {
        if (!Directory.Exists(_logDirectory)) return Task.FromResult(0);
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
        var files = Directory.GetFiles(_logDirectory, "*.json")
            .Concat(Directory.GetFiles(_logDirectory, "*.log"));

        var deleted = 0;
        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(filePath);
            if (info.LastWriteTimeUtc < cutoff)
            {
                try
                {
                    info.Delete();
                    deleted++;
                    _logger.LogInformation("Purged old log file {FileName}", info.Name);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to purge log file {FileName}", info.Name);
                }
            }
        }
        return Task.FromResult(deleted);
    }

    private static bool MatchesFilter(SystemLogEntryDto entry, string? level, string? search, DateTime? dateFrom, DateTime? dateTo)
    {
        if (!string.IsNullOrWhiteSpace(level) && !entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
            return false;
        if (dateFrom.HasValue && entry.Timestamp < dateFrom.Value) return false;
        if (dateTo.HasValue && entry.Timestamp > dateTo.Value) return false;
        if (!string.IsNullOrWhiteSpace(search) &&
            !entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) &&
            !(entry.Category?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) &&
            !(entry.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) &&
            !(entry.CorrelationId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;
        return true;
    }

    private List<string> GetTargetFiles(string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var sanitized = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(sanitized) || sanitized != fileName)
                throw new BadRequestException("Invalid file name.");
            var filePath = Path.Combine(_logDirectory, sanitized);
            return File.Exists(filePath) ? [filePath] : [];
        }
        return [.. Directory.GetFiles(_logDirectory, "*.json")
            .Concat(Directory.GetFiles(_logDirectory, "*.log"))
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)];
    }

    private async IAsyncEnumerable<SystemLogEntryDto> StreamLogFileAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = ParseLogLine(line);
            if (entry is not null) yield return entry;
        }
    }

    private SystemLogEntryDto? ParseLogLine(string line)
    {
        if (line.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                return new SystemLogEntryDto(
                    Id: 0,
                    Timestamp: root.TryGetProperty("Timestamp", out var ts) && ts.TryGetDateTimeOffset(out var dto)
                        ? dto.UtcDateTime : _timeProvider.GetUtcNow().UtcDateTime,
                    Level: root.TryGetProperty("Level", out var lvl) ? lvl.GetString() ?? "Information" : "Information",
                    Category: root.TryGetProperty("SourceContext", out var cat) ? cat.GetString() ?? "" : "",
                    Message: root.TryGetProperty("RenderedMessage", out var msg) ? msg.GetString() ?? ""
                        : root.TryGetProperty("Message", out var msg2) ? msg2.GetString() ?? "" : "",
                    Exception: root.TryGetProperty("Exception", out var ex) ? ex.GetString() : null,
                    CorrelationId: root.TryGetProperty("CorrelationId", out var cid) ? cid.GetString() : null);
            }
            catch (JsonException ex)
            {
                // Malformed JSON - fall through to plain-text parse.
                System.Diagnostics.Debug.WriteLine($"[SystemLogService] Malformed JSON log line: {ex.Message}");
            }
        }
        return ParsePlainTextLine(line);
    }

    private SystemLogEntryDto? ParsePlainTextLine(string line)
    {
        var timestamp = _timeProvider.GetUtcNow().UtcDateTime;
        var level = "Information";
        var message = line;
        if (line.Length >= 23 && DateTime.TryParseExact(
                line[..23], "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            timestamp = parsed;
            var rest = line[23..].TrimStart();
            (level, message) = SplitLevel(rest, level);
        }
        return new SystemLogEntryDto(0, timestamp, level, "", message, null, null);
    }

    private static readonly Dictionary<string, string> LevelNames = new(StringComparer.Ordinal)
    {
        ["VRB"] = "Verbose",
        ["VERBOSE"] = "Verbose",
        ["DBG"] = "Debug",
        ["DEBUG"] = "Debug",
        ["INF"] = "Information",
        ["INFORMATION"] = "Information",
        ["WRN"] = "Warning",
        ["WARNING"] = "Warning",
        ["ERR"] = "Error",
        ["ERROR"] = "Error",
        ["FTL"] = "Fatal",
        ["FATAL"] = "Fatal"
    };

    /// <summary>Reads a leading <c>[LEVEL]</c> tag: a known code is named in full, any other code is kept
    /// as written. Without a tag the level stays <paramref name="defaultLevel"/> and the whole text is the
    /// message.</summary>
    private static (string Level, string Message) SplitLevel(string rest, string defaultLevel)
    {
        if (!rest.StartsWith('[') || rest.IndexOf(']') is not (var idx and > 0)) return (defaultLevel, rest);
        var levelCode = rest[1..idx];
        return (LevelNames.GetValueOrDefault(levelCode, levelCode), rest[(idx + 1)..].TrimStart());
    }
}
