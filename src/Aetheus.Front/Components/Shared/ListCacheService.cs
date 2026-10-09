// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Runtime.CompilerServices;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Lightweight client-side cache for list views (stale-while-revalidate). A shared list component
/// stores its last loaded page under a stable key (route + filter), so the next time the user lands
/// on that list the previous data renders <b>immediately</b> while a fresh fetch runs in the
/// background - no full-screen spinner / flash on every navigation. Registered as a singleton so the
/// cache survives navigations within the Blazor WASM session.
/// <para>
/// Entries carry the time they were stored so a consumer can additionally treat a recent entry as
/// fresh (<see cref="IsFresh"/>) and skip the background re-fetch entirely - useful for views that
/// are re-entered very frequently (e.g. project Overview) where a network round-trip on every visit
/// is wasteful.
/// </para>
/// <para>
/// FRONT-020: entries belong to the signed-in account. Every key is stored under the account that
/// asked for it, and <see cref="Clear"/> opens a new session: a response still in flight when the
/// user signs out (or switches organization) is neither stored nor applied, so it can never re-seed the
/// previous account's rows for the next one.
/// </para>
/// </summary>
public sealed class ListCacheService(TimeProvider timeProvider, AuthStateProvider? auth = null)
{
    /// <summary>APR9: cache key for the parsed OpenAPI endpoints on the API Reference page. Centralised
    /// here so the version-check loop (MainLayout) can invalidate it the instant a backend redeploy is
    /// detected - the page then auto-refetches the fresh spec on its next visit, no manual refresh.</summary>
    public const string ApiReferenceKey = "api-reference:endpoints";

    /// <summary>S-TECH-SWEV: hard cap on cached entries. The SWR layer mints one entry per
    /// (list × page × filter set) across ~10 lists; without a bound a long session that pages through
    /// many filter combinations would grow the dictionary unbounded (it was only ever emptied on
    /// org-switch / sign-out). When the cap is exceeded the least-recently-stored entries are evicted.</summary>
    private const int MaxEntries = 200;

    /// <summary>S-TECH-SWEV: absolute staleness ceiling. An entry older than this is treated as a miss
    /// (and dropped) on read, so a list left untouched for a long session never seeds ancient rows.</summary>
    private static readonly TimeSpan AbsoluteTtl = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<object, RevalidationVersion> _revalidationVersions = new();
    private readonly object _staticRevalidationOwner = new();
    private long _session;

    private readonly record struct Entry(object Value, DateTimeOffset StoredAt);
    private sealed class RevalidationVersion { public long Value; }

    /// <summary>The account the entries are read and written for; empty when nobody is signed in.</summary>
    private string CurrentScope => auth?.Username ?? string.Empty;

    private static string Scoped(string scope, string key) => scope + "\u001f" + key;

    /// <summary>Returns the last cached value for <paramref name="key"/>, or default when none/typed-mismatch.</summary>
    public bool TryGet<T>(string key, out T? value)
    {
        key = Scoped(CurrentScope, key);
        if (_entries.TryGetValue(key, out var entry) && entry.Value is T typed)
        {
            // S-TECH-SWEV: lazily drop entries past the absolute TTL so stale data never seeds.
            if (timeProvider.GetUtcNow() - entry.StoredAt >= AbsoluteTtl)
            {
                _entries.Remove(key);
                value = default;
                return false;
            }
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// FRONT-020: the account and the session a request is made for. Take it BEFORE the request and hand
    /// it to <see cref="TryStore{T}"/> with the answer: an answer that arrives after a sign-out, a rejected
    /// token or an organization switch (<see cref="Clear"/>) is then neither stored nor applied.
    /// </summary>
    public readonly record struct Ticket(string Scope, long Session);

    /// <summary>The ticket of a request about to be sent (see <see cref="Ticket"/>).</summary>
    public Ticket Begin() => new(CurrentScope, Volatile.Read(ref _session));

    /// <summary>True while the session <paramref name="ticket"/> was taken in still lasts.</summary>
    public bool IsCurrent(Ticket ticket) => Volatile.Read(ref _session) == ticket.Session;

    /// <summary>
    /// Stores the answer to a request under <paramref name="key"/> for the account the ticket was taken
    /// for (a null answer stores nothing), and says whether the caller may apply it: false once the
    /// session of the ticket is over, in which case nothing was stored and the answer must be dropped.
    /// The only way to write the cache, so every account-scoped writer goes through the same check.
    /// </summary>
    public bool TryStore<T>(Ticket ticket, string key, T? value) where T : class
    {
        if (!IsCurrent(ticket)) return false;
        if (value is not null) SetScoped(ticket.Scope, key, value);
        return true;
    }

    private void SetScoped<T>(string scope, string key, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _entries[Scoped(scope, key)] = new Entry(value, timeProvider.GetUtcNow());
        if (_entries.Count > MaxEntries)
            EvictOldest();
    }

    /// <summary>S-TECH-SWEV: trims the cache back under <see cref="MaxEntries"/> by removing the
    /// least-recently-stored entries (each background revalidation re-stamps a live entry, so hot
    /// lists keep a recent timestamp and survive while cold filter combinations age out first).</summary>
    private void EvictOldest()
    {
        var overflow = _entries.Count - MaxEntries;
        if (overflow <= 0) return;

        var stale = _entries
            .OrderBy(kv => kv.Value.StoredAt)
            .Take(overflow)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
            _entries.Remove(key);
    }

    /// <summary>
    /// True when an entry exists for <paramref name="key"/> and was stored less than
    /// <paramref name="maxAge"/> ago - i.e. recent enough to render without a background re-fetch.
    /// </summary>
    public bool IsFresh(string key, TimeSpan maxAge) =>
        _entries.TryGetValue(Scoped(CurrentScope, key), out var entry) && timeProvider.GetUtcNow() - entry.StoredAt < maxAge;

    /// <summary>
    /// Pre-applies the cached value for <paramref name="key"/> (if any) WITHOUT a fetch: call from a
    /// component's init so the very first paint shows the previous rows instead of an empty grid /
    /// spinner. Pair with <see cref="RevalidateAsync{T}"/> in the grid's <c>OnLoadData</c>.
    /// </summary>
    public void Seed<T>(string key, Action<T> apply) where T : class
    {
        if (TryGet<T>(key, out var cached) && cached is not null) apply(cached);
    }

    /// <summary>
    /// Stale-while-revalidate load for a server-paginated grid view. When <paramref name="key"/> is
    /// already cached, applies it immediately with NO spinner; otherwise shows the spinner for the cold
    /// fetch. Then it always refetches in the background and overwrites with fresh data, so realtime
    /// freshness is preserved (the cache only smooths the paint, it never replaces a live load). The
    /// expired-JWT <see cref="HttpRequestException"/> (→ AuthProvider redirect) is swallowed, matching
    /// the existing <c>catch (HttpRequestException)</c> at every call site.
    /// </summary>
    /// <param name="key">Stable cache key encoding the full query (page + size + every filter + sort).</param>
    /// <param name="fetch">The live fetch (e.g. an <c>Api.Domain.GetXAsync(...)</c> call).</param>
    /// <param name="apply">Assigns the result to the component (items + total count).</param>
    /// <param name="setLoading">Sets the component's loading flag (drives the grid spinner).</param>
    /// <param name="render">Triggers a re-render (typically <c>() => InvokeAsync(StateHasChanged)</c>).</param>
    public async Task RevalidateAsync<T>(string key, Func<Task<T>> fetch, Action<T> apply,
        Action<bool> setLoading, Func<Task> render, Action<HttpRequestException>? onError = null) where T : class
    {
        var owner = apply.Target ?? setLoading.Target ?? _staticRevalidationOwner;
        var version = _revalidationVersions.GetValue(owner, _ => new RevalidationVersion());
        var requestVersion = Interlocked.Increment(ref version.Value);
        bool IsCurrent() => Volatile.Read(ref version.Value) == requestVersion;
        // FRONT-020: the account and session the request was made for, read before the fetch.
        var ticket = Begin();

        var hit = TryGet<T>(key, out var cached) && cached is not null;
        if (hit) apply(cached!);
        setLoading(!hit);
        await render();

        try
        {
            var fresh = await fetch();
            // Signed out (or switched organization) while the request was in flight: the answer belongs to
            // the previous session, so it is neither cached nor shown.
            if (TryStore(ticket, key, fresh) && IsCurrent()) apply(fresh);
        }
        catch (HttpRequestException ex)
        {
            // Authentication failures are owned by AuthProvider. Every other transport/backend failure
            // must be observable so the list can keep stale rows while offering a truthful Retry state.
            if (ex.StatusCode != HttpStatusCode.Unauthorized)
                onError?.Invoke(ex);
        }
        if (!IsCurrent()) return;
        setLoading(false);
        await render();
    }

    /// <summary>Drops a single cached entry (e.g. after a mutation invalidates it).</summary>
    public void Invalidate(string key) => _entries.Remove(Scoped(CurrentScope, key));

    /// <summary>
    /// S-TECH-SWIV: drops every cached page whose key starts with <paramref name="prefix"/>. Call after a
    /// local mutation (delete/create) so the SWR seed cannot momentarily re-render a row that was just
    /// removed (cache-hit on the pre-mutation page before revalidation catches up). The list keys encode
    /// the resource as their first segment (e.g. <c>"vaults:"</c>, <c>"releases:paged:"</c>).
    /// </summary>
    public void InvalidatePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;
        prefix = Scoped(CurrentScope, prefix);
        var matches = _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in matches)
            _entries.Remove(key);
    }

    /// <summary>
    /// Clears the whole cache (e.g. on sign-out / organization switch) and starts a new session, so a
    /// request issued before the call cannot store its answer afterwards.
    /// </summary>
    public void Clear()
    {
        Interlocked.Increment(ref _session);
        _entries.Clear();
    }
}
