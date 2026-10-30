// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Tells a page that a pipeline was created, updated or deleted, through the scoped <c>entities</c> hub
/// the pipeline pages already use. A library or vault page asks "used by" once when it opens (R-64), so a
/// pipeline created afterwards (the setup wizard does) left it saying "Unused" until a full reload.
/// <paramref name="onChanged"/> runs on every pipeline push and again after a reconnect (group
/// membership is per connection and lost then). An unavailable hub leaves the page on what it loaded.
/// </summary>
internal sealed class PipelineChangeSubscription(HubConnectionFactory hubFactory, Func<Task> onChanged)
    : IAsyncDisposable
{
    private HubConnection? _hub;

    public async Task StartAsync()
    {
        try
        {
            var hub = hubFactory.Create("entities");
            _hub = hub;
            hub.On<ResourceType, int, string>("EntityChanged", (type, _, _) =>
                type == ResourceType.Pipeline ? onChanged() : Task.CompletedTask);
            hub.RejoinOnReconnect(async () =>
            {
                await hub.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline);
                await onChanged();
            });
            await hub.StartAsync();
            await hub.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException
            or Microsoft.AspNetCore.SignalR.HubException)
        {
            // Hub unavailable: the page keeps the usage it loaded.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not { } hub) return;
        _hub = null;
        await hub.LeaveEntityUpdatesAndDisposeAsync(ResourceType.Pipeline);
    }
}
