// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary> The columns of a server's task log grid. The grid reads the log page by page from the
/// API, so its header filters and sorts are applied here, to every entry of the server, and not to
/// the entries already on screen. </summary>
internal static class ServerLogQuery
{
    internal static readonly GridQueryMap<TaskLog> Columns = new GridQueryMap<TaskLog>()
        .Date("timestamp", l => l.Timestamp)
        .Enum("level", l => l.Level)
        .Text("message", l => l.Message);

    /// <summary>The sort keys of a request: its <c>Sorts</c>, else its single <c>SortBy</c>, else none.</summary>
    internal static IReadOnlyList<GridSort>? SortsOf(PaginationRequest request) =>
        request.Sorts is { Count: > 0 } sorts
            ? sorts
            : string.IsNullOrWhiteSpace(request.SortBy)
                ? null
                : [new GridSort { Field = request.SortBy, Descending = request.SortDescending }];
}
