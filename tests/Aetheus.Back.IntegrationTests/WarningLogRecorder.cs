// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-1, the guard that keeps the system logs at zero: every line a test host writes at Warning or above, with its category
/// and exception. A test marks the position before a healthy journey and asserts none came after it; a line that is a
/// legitimate outcome of the journey (a refusal the test provokes on purpose) is not a healthy journey and belongs in a
/// test that says so.
/// </summary>
public sealed class WarningLogRecorder : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];

    public int Mark()
    {
        lock (_gate) return _lines.Count;
    }

    public IReadOnlyList<string> Since(int mark)
    {
        lock (_gate) return [.. _lines.Skip(mark)];
    }

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose()
    {
    }

    private void Record(string line)
    {
        lock (_gate) _lines.Add(line);
    }

    private sealed class Recorder(WarningLogRecorder owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            owner.Record($"{logLevel} {category}: {message}{(exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message)}");
        }
    }
}
