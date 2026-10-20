// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Notify Items-based grids before replacing live rows so new entries can be highlighted.
/// The initial list for a server establishes the baseline without highlighting.</summary>
internal sealed class LiveGridRows
{
    private object? _held;
    private object? _scope;

    public Task ObserveAsync<TItem>(OmniDataGrid<TItem>? grid, object? rows, object? scope = null)
    {
        if (rows is null || ReferenceEquals(_held, rows)) return Task.CompletedTask;

        var initial = _held is null || !Equals(_scope, scope);
        _held = rows;
        _scope = scope;
        return initial || grid is null ? Task.CompletedTask : grid.RefreshAsync();
    }
}
