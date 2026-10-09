// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Git;

/// <summary>
/// One server-paged list of the repository page (commits, pull requests): the rows its grid shows, their
/// total, and the loading and error states its tab renders. Recette R-327: the grid scrolls (remote
/// virtualization), so <see cref="Items"/> is the block the grid asked for last.
/// FRONT-003: only the latest request applies its answer. <see cref="Clear"/> (the page switching to another
/// repository) and every new load retire the requests still in flight, so a late answer from the previous
/// repository, or from an older block, never overwrites the rows on screen.
/// </summary>
/// <param name="isLoadFailure">The exceptions the tab reports as a failed load; any other one propagates.</param>
internal sealed class GitPagedList<T>(Func<Exception, bool> isLoadFailure)
{
    private int _request;

    public List<T> Items { get; private set; } = [];
    public int TotalCount { get; private set; }
    public bool Loading { get; private set; }
    public bool Error { get; private set; }

    public void Clear()
    {
        _request++;
        Items = [];
        TotalCount = 0;
        Loading = false;
    }

    /// <summary>A page-level load (first load, retry): straight from the server, a failure empties the
    /// list and raises the error state.</summary>
    public async Task LoadAsync(Func<Task<PaginatedResult<T>>> fetch)
    {
        var request = ++_request;
        Loading = true;
        Error = false;
        try
        {
            var result = await fetch();
            if (request != _request) return;
            Apply(result);
        }
        catch (Exception ex) when (isLoadFailure(ex))
        {
            if (request != _request) return;
            Items = [];
            TotalCount = 0;
            Error = true;
        }
        finally
        {
            if (request == _request) Loading = false;
        }
    }

    /// <summary>
    /// A block asked by the grid (S-TECH-SWGT): the cached copy paints at once, then the server revalidates
    /// it. A failure after a cache hit keeps the cached rows on screen and still raises the error state.
    /// </summary>
    public async Task LoadCachedAsync(ListCacheService cache, string key, Func<Task<PaginatedResult<T>>> fetch)
    {
        var request = ++_request;
        var hit = cache.TryGet<PaginatedResult<T>>(key, out var cached) && cached is not null;
        if (hit) Apply(cached!);
        Loading = !hit;
        Error = false;
        // FRONT-020: taken before the request; an answer arriving after a sign-out is neither cached nor shown.
        var ticket = cache.Begin();
        try
        {
            var result = await fetch();
            // The answer still belongs to its key: cache it even when a newer request superseded it.
            if (!cache.TryStore(ticket, key, result)) return;
            if (request != _request) return;
            Apply(result);
        }
        catch (Exception ex) when (isLoadFailure(ex))
        {
            if (request != _request) return;
            if (!hit)
            {
                Items = [];
                TotalCount = 0;
            }
            Error = true;
        }
        finally
        {
            if (request == _request) Loading = false;
        }
    }

    private void Apply(PaginatedResult<T> result)
    {
        Items = result.Items;
        TotalCount = result.TotalCount;
    }
}
