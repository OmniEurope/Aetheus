// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Own one admin-hub subscription per page and dispatch only matching entity changes so
/// lists share consistent refresh behavior; invoke catch-up once after reconnect and dispose with
/// the page.</summary>
public sealed class AdminEntitySubscription(HubConnectionFactory hubFactory) : IAsyncDisposable
{
    private HubConnection? _hub;

    /// <summary>Starts listening for changes to <paramref name="entityName"/> (an <c>AdminEntities</c>
    /// constant) and invokes <paramref name="onChanged"/> on each matching broadcast.</summary>
    public Task StartAsync(string entityName, Func<Task> onChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return StartAsync([entityName], onChanged);
    }

    /// <summary>Same, for a view that depends on several entities (a user's effective permissions
    /// follow users, roles and organizations): one hub connection, any listed name triggers.</summary>
    public async Task StartAsync(IReadOnlyCollection<string> entityNames, Func<Task> onChanged)
    {
        ArgumentNullException.ThrowIfNull(entityNames);
        if (entityNames.Count == 0 || entityNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one entity name, none blank.", nameof(entityNames));
        ArgumentNullException.ThrowIfNull(onChanged);
        var watched = new HashSet<string>(entityNames, StringComparer.Ordinal);
        try
        {
            _hub = hubFactory.Create("admin");
            _hub.On<string, int, string>("AdminEntityChanged", (entity, _, _) =>
                watched.Contains(entity) ? onChanged() : Task.CompletedTask);
            _hub.Reconnected += async _ =>
            {
                try { await onChanged().ConfigureAwait(false); }
                catch { /* reconnect reconciliation is best-effort */ }
            };
            using var timeout = new CancellationTokenSource(FrontendRuntimeDefaults.SignalRStartTimeout, hubFactory.Clock);
            await _hub.StartAsync(timeout.Token);
        }
        catch
        {
            // Hub unreachable at start: the page keeps what it loaded, without live updates until it
            // is opened again (best effort, as before; the removed polling fallback had no caller).
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
            _hub = null;
        }
    }
}
