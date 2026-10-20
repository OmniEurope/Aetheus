// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>Error-grid filters and sorting apply server-side to all stored groups through an
/// explicit column-key map.</summary>
internal static class AppErrorQuery
{
    /// <summary>Most exception types the type column's checkable list offers.</summary>
    internal const int MaximumExceptionTypes = 500;

    /// <summary>The header filters: exception type (checkable list), message text, count, first and last seen.</summary>
    internal static readonly GridQueryMap<AppErrorEvent> Filters = Columns();

    /// <summary>The sortable columns, the same keys as the filters.</summary>
    internal static readonly GridQueryMap<AppErrorEvent> Sorts = Columns();

    /// <summary>The single sort a request carries, or none when it names no column.</summary>
    internal static IReadOnlyList<GridSort>? SortsOf(string? sortBy, bool sortDescending) =>
        AppLogQuery.SortsOf(sortBy, sortDescending);

    private static GridQueryMap<AppErrorEvent> Columns() => new GridQueryMap<AppErrorEvent>()
        .Text("exceptionType", e => e.ExceptionType)
        .Text("message", e => e.Message)
        .Number("occurrenceCount", e => e.OccurrenceCount)
        .Date("firstSeenAt", e => e.FirstSeenAt)
        .Date("lastSeenAt", e => e.LastSeenAt);
}
