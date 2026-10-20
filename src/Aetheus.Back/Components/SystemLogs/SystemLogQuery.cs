// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>Apply allowed sort and filter keys to the complete log selection before virtualized
/// paging, never only to the visible block.</summary>
internal static class SystemLogQuery
{
    internal static readonly GridQueryMap<SystemLogEntryDto> Columns = new GridQueryMap<SystemLogEntryDto>()
        .Date("timestamp", entry => entry.Timestamp)
        .Text("level", entry => entry.Level)
        .Text("category", entry => entry.Category)
        .Text("message", entry => entry.Message)
        .Text("correlationId", entry => entry.CorrelationId);

    /// <summary>The single sort a request carries, or none when it names no column.</summary>
    internal static IReadOnlyList<GridSort>? SortsOf(string? sortBy, bool sortDescending) =>
        string.IsNullOrWhiteSpace(sortBy)
            ? null
            : [new GridSort { Field = sortBy.Trim(), Descending = sortDescending }];
}
