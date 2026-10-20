// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Tests;

/// <summary>A logger that keeps every entry with its level and formatted message, for level assertions.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly List<LogRecord> _records = [];

    /// <summary>Every entry with its exception, for tests that assert what was reported.</summary>
    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_gate)
                return [.. _records];
        }
    }

    internal sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
                return [.. _entries];
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            var message = formatter(state, exception);
            _entries.Add((logLevel, message));
            _records.Add(new LogRecord(logLevel, message, exception));
        }
    }
}
