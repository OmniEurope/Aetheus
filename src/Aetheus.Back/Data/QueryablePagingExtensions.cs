// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data;

/// <summary>What a server-paged list needs besides its page: the total.</summary>
public static class QueryablePagingExtensions
{
    /// <summary>
    /// The total behind a page that was just read, without a <c>COUNT</c> when the page itself proves it.
    /// <para>A page that holds rows but fewer than <paramref name="pageSize"/>, or an empty first page, is the
    /// last page: the total is the rows before it plus the rows read, and counting the filtered set again would
    /// scan it a second time for a number already in hand. Any other page (a full one, or an empty page beyond
    /// the end) cannot say how many rows follow it, so the filtered set is counted as before.</para>
    /// <para>The page is read first; a row inserted between the two reads makes the total as stale as it was
    /// between the two commands of a count-then-page read.</para>
    /// </summary>
    /// <param name="filtered">The query the page was cut from, filters applied and no ordering, paging or
    /// projection needed.</param>
    /// <param name="page">The 1-based page that was read.</param>
    /// <param name="pageSize">The size the page was cut with.</param>
    /// <param name="rowsRead">How many rows the page held.</param>
    public static async Task<int> CountGivenPageAsync<T>(
        this IQueryable<T> filtered, int page, int pageSize, int rowsRead, CancellationToken ct)
    {
        if (rowsRead < pageSize && (rowsRead > 0 || page == 1))
            return (page - 1) * pageSize + rowsRead;
        return await filtered.CountAsync(ct).ConfigureAwait(false);
    }
}
