// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The rows a page changed after an action answered (R-10): a row saved or created shows what the server
/// returned, a row deleted is gone, both at once and before any read of the list ends. The changes are laid
/// over whatever the page holds each time the grid is served, so a read that was already in flight when the
/// action answered cannot bring the old row back; a change is dropped once a read that started after it has
/// returned (the server's rows then include it).
/// </summary>
internal sealed class GridRowOverlay<TItem> where TItem : notnull
{
    private readonly List<Change> _changes = [];
    private long _sequence;

    /// <summary>The number of the last change, taken when a read starts: that read settles every change up to it.</summary>
    public long Sequence => _sequence;

    public void Show(object key, TItem row, Comparison<TItem>? order) =>
        _changes.Add(new Change(++_sequence, key, Remove: false, row, order));

    public void Hide(object key, TItem row) =>
        _changes.Add(new Change(++_sequence, key, Remove: true, row, null));

    /// <summary>A read that started at <paramref name="startedAt"/> has returned: the changes up to it are in its rows.</summary>
    public void Settle(long startedAt) => _changes.RemoveAll(change => change.Sequence <= startedAt);

    public void Clear() => _changes.Clear();

    /// <summary>
    /// The rows of the page with the changes laid over them. A row already there is replaced or removed. A new
    /// row is added on the first window only (the server places it for the others), where its order puts it,
    /// else on top; a window that is not virtualized keeps its page size. The total moves only for a row this
    /// window proves present or added, so a change the window cannot see never counts twice.
    /// </summary>
    public OmniDataGridResult<TItem> Apply(
        OmniDataGridResult<TItem> held, int skip, int pageSize, bool virtualized, Func<TItem, object?> keyOf)
    {
        if (_changes.Count == 0) return held;
        var rows = held.Items.ToList();
        var total = held.TotalCount;
        foreach (var change in _changes)
        {
            var index = rows.FindIndex(row => Equals(keyOf(row), change.Key));
            if (change.Remove)
            {
                if (index < 0) continue;
                rows.RemoveAt(index);
                total = Math.Max(0, total - 1);
            }
            else if (index >= 0)
            {
                rows[index] = change.Row;
            }
            else if (skip == 0)
            {
                rows.Insert(InsertionIndex(rows, change), change.Row);
                total++;
            }
        }

        if (!virtualized && pageSize > 0 && rows.Count > pageSize) rows.RemoveRange(pageSize, rows.Count - pageSize);
        return new OmniDataGridResult<TItem>(rows, total);
    }

    private static int InsertionIndex(List<TItem> rows, Change change)
    {
        if (change.Order is null) return 0;
        var index = rows.FindIndex(row => change.Order(change.Row, row) < 0);
        return index < 0 ? rows.Count : index;
    }

    private sealed record Change(long Sequence, object Key, bool Remove, TItem Row, Comparison<TItem>? Order);
}
