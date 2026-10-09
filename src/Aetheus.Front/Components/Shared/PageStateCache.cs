// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Recette R2-087 (decided 2026-10-03): a page the reader comes back to shows at once what it showed,
/// then refreshes behind it; the page-wide loader stays for a first visit only.
/// <para>
/// A page remembers its last loaded state under its address (the route it renders, e.g.
/// <c>pipelines/12</c>) and asks for it before loading. The entries live in
/// <see cref="ListCacheService"/>: in this browser tab's memory only, for the signed-in session (the
/// layout clears it on sign-out, on a rejected token and on an organization switch), dropped after 30
/// minutes and capped in number. Nothing is written to storage or shared with another user.
/// </para>
/// <para>
/// The scroll position comes back separately and for every page: the shell's
/// <c>Aetheus.pageScroll</c> helper restores it on a return (browser back, or "Revenir" to the page
/// just left).
/// </para>
/// </summary>
public sealed class PageStateCache(ListCacheService cache)
{
    /// <summary>The last state remembered for <paramref name="address"/>, if any and still recent.</summary>
    public bool TryRestore<T>(string address, out T state) where T : class
    {
        var found = cache.TryGet<T>(Key<T>(address), out var cached) && cached is not null;
        state = cached!;
        return found;
    }

    /// <summary>FRONT-020: the ticket of the load about to run; see <see cref="ListCacheService.Ticket"/>.</summary>
    public ListCacheService.Ticket Begin() => cache.Begin();

    /// <summary>Remembers what the page at <paramref name="address"/> now shows, unless the session the
    /// load began in is over (signed out meanwhile): false then, and nothing is remembered.</summary>
    public bool Remember<T>(ListCacheService.Ticket ticket, string address, T state) where T : class =>
        cache.TryStore(ticket, Key<T>(address), state);

    /// <summary>Forgets the state of <paramref name="address"/> (the entity it shows is gone).</summary>
    public void Forget<T>(string address) where T : class => cache.Invalidate(Key<T>(address));

    private static string Key<T>(string address) => $"page:{address}:{typeof(T).FullName}";
}
