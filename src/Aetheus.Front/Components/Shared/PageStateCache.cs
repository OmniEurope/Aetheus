// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>Restore recent page state from this tab's session-scoped, bounded memory cache so
/// return navigation can refresh without a full-page loader; clear it across sign-out, token
/// rejection and organization changes, with scroll restoration owned separately by the
/// shell.</summary>
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
