// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-2, guard of the system log staying at zero: the three Entity Framework warnings about the shape of a query
/// (<see cref="CoreEventId.RowLimitingOperationWithoutOrderByWarning"/>, <see cref="CoreEventId.FirstWithoutOrderByAndFilterWarning"/>,
/// <see cref="RelationalEventId.MultipleCollectionIncludeWarning"/>) reach production as nameless Warning lines (20 of them in the
/// 2026-10-29 export) because they are only logged. Every integration host throws them on the application's context instead,
/// so the query that raises one fails its request with its own stack, and this log records any of them that still reaches a
/// log (a background loop that catches the exception and logs it), to be asserted when the host is disposed.
/// </summary>
public sealed class QueryShapeWarningLog : ILoggerProvider, ILogger
{
    private static readonly string[] WarningNames =
    [
        nameof(CoreEventId.RowLimitingOperationWithoutOrderByWarning),
        nameof(CoreEventId.FirstWithoutOrderByAndFilterWarning),
        nameof(RelationalEventId.MultipleCollectionIncludeWarning)
    ];

    private readonly object _gate = new();
    private readonly List<string> _raised = [];

    /// <summary>Throws the three warnings instead of logging them.</summary>
    public static void ThrowOnQueryShapeWarnings(DbContextOptionsBuilder options) =>
        options.ConfigureWarnings(warnings => warnings.Throw(
            CoreEventId.RowLimitingOperationWithoutOrderByWarning,
            CoreEventId.FirstWithoutOrderByAndFilterWarning,
            RelationalEventId.MultipleCollectionIncludeWarning));

    public void AssertNone()
    {
        string[] raised;
        lock (_gate) raised = [.. _raised];
        Assert.True(raised.Length == 0, "A query shape warning was raised:\n" + string.Join("\n---\n", raised));
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel < LogLevel.Warning) return;
        var message = formatter(state, exception);
        var text = message + " " + exception;
        if (!WarningNames.Any(name => text.Contains(name, StringComparison.Ordinal)) && !IsOneOfTheWarnings(eventId))
            return;
        lock (_gate) _raised.Add($"{message}\n{exception}\n{new StackTrace(1, fNeedFileInfo: false)}");
    }

    private static bool IsOneOfTheWarnings(EventId eventId) =>
        WarningNames.Any(name => eventId.Name?.EndsWith(name, StringComparison.Ordinal) == true);

    public void Dispose()
    {
    }
}
