// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Aetheus.Back.Middleware;

/// <summary>
/// JSON file logger with a single background-drained channel writer. Log calls
/// only enqueue (non-blocking, drop-on-overflow) so request threads are never
/// blocked on disk I/O - the previous implementation serialized every write
/// through one lock + synchronous <c>File.AppendAllText</c>.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _stem;
    private readonly LogLevel _minLevel;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, JsonFileLogger> _loggers = new();
    private readonly Channel<string> _channel;
    private readonly Task _writerTask;
    private readonly LoggerDropCounter _dropCounter = new();

    public long DroppedEntries => Interlocked.Read(ref _dropCounter.Total);

    public JsonFileLoggerProvider(
        string directory,
        LogLevel minLevel = LogLevel.Information,
        TimeProvider? timeProvider = null,
        string? instance = null)
        : this(directory, minLevel, timeProvider, 8192, null, instance)
    {
    }

    internal JsonFileLoggerProvider(
        string directory,
        LogLevel minLevel,
        TimeProvider? timeProvider,
        int capacity,
        Task? drainGate,
        string? instance = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _directory = directory;
        _stem = FileStem(instance);
        _minLevel = minLevel;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);

        // Drop-on-full: a logging storm must never block or OOM the app; losing
        // some lines under extreme pressure beats stalling request threads.
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            // Wait mode makes TryWrite return false when full; Log remains non-blocking and can count
            // every rejected entry instead of silently losing data inside the channel implementation.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(async () =>
        {
            if (drainGate is not null)
                await drainGate.ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
        });
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new JsonFileLogger(name, _minLevel, _channel.Writer, _timeProvider, _dropCounter));

    private async Task DrainAsync()
    {
        var reader = _channel.Reader;
        var sb = new StringBuilder();
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                sb.Clear();
                while (reader.TryRead(out var line))
                    sb.Append(line).Append('\n');

                var dropped = Interlocked.Exchange(ref _dropCounter.SinceLastWarning, 0);
                if (dropped > 0)
                {
                    sb.Append(JsonSerializer.Serialize(new
                    {
                        Timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("O"),
                        Level = "Warning",
                        SourceContext = nameof(JsonFileLoggerProvider),
                        Message = $"File logger saturated: {dropped} entries were dropped (total {DroppedEntries}).",
                        Exception = (string?)null
                    })).Append('\n');
                }

                if (sb.Length == 0) continue;
                CleanupOldLogs();
                var filePath = ResolveLogFilePath();
                try
                {
                    await File.AppendAllTextAsync(filePath, sb.ToString()).ConfigureAwait(false);
                }
                catch
                {
                    // A logging sink must never throw into the app.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>The file name prefix: <c>aetheus</c>, or <c>aetheus-{instance}</c> so two processes sharing one
    /// log volume (the blue and the green back-end) never append to the same file.</summary>
    internal static string FileStem(string? instance)
    {
        var clean = new string((instance ?? string.Empty)
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            .ToArray()).Trim('.', '-', '_');
        return clean.Length == 0 ? "aetheus" : $"aetheus-{clean}";
    }

    private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100 MB
    private const int RetentionDays = 30;
    private DateTime _lastCleanup = DateTime.MinValue;

    private void CleanupOldLogs()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if ((now - _lastCleanup).TotalHours < 1) return;
        _lastCleanup = now;
        try
        {
            var cutoff = now.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(_directory, "aetheus-*.json"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* cleanup is best-effort */ }
    }

    private string ResolveLogFilePath()
    {
        var dateStr = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");
        var basePath = Path.Combine(_directory, $"{_stem}-{dateStr}.json");
        if (!File.Exists(basePath) || new FileInfo(basePath).Length < MaxFileSizeBytes)
            return basePath;
        for (var i = 1; i < 100; i++)
        {
            var rotated = Path.Combine(_directory, $"{_stem}-{dateStr}.{i}.json");
            if (!File.Exists(rotated) || new FileInfo(rotated).Length < MaxFileSizeBytes)
                return rotated;
        }
        return basePath;
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _writerTask.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* best-effort flush on shutdown */ }
        _loggers.Clear();
    }
}

internal sealed class LoggerDropCounter
{
    public long Total;
    public long SinceLastWarning;
}

internal sealed class JsonFileLogger(
    string categoryName,
    LogLevel minLevel,
    ChannelWriter<string> writer,
    TimeProvider timeProvider,
    LoggerDropCounter dropCounter) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var entry = new
        {
            Timestamp = timeProvider.GetUtcNow().UtcDateTime.ToString("O"),
            Level = logLevel switch
            {
                LogLevel.Trace => "Verbose",
                LogLevel.Debug => "Debug",
                LogLevel.Information => "Information",
                LogLevel.Warning => "Warning",
                LogLevel.Error => "Error",
                LogLevel.Critical => "Fatal",
                _ => logLevel.ToString()
            },
            SourceContext = categoryName,
            Message = formatter(state, exception),
            Exception = exception?.ToString(),
            CorrelationId = GetProperty(state, "CorrelationId")
        };

        // Non-blocking enqueue; drops if the bounded channel is saturated.
        if (!writer.TryWrite(JsonSerializer.Serialize(entry, JsonSerializerOptions.Default)))
        {
            Interlocked.Increment(ref dropCounter.Total);
            Interlocked.Increment(ref dropCounter.SinceLastWarning);
        }
    }

    private static string? GetProperty<TState>(TState state, string name)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
            return null;
        var value = properties.FirstOrDefault(property =>
            string.Equals(property.Key, name, StringComparison.Ordinal)).Value;
        return value?.ToString();
    }
}
