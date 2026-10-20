// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.Services.DomainEvents;

/// <summary>
/// Resolves all <see cref="IDomainEventHandler{TEvent}"/> instances from the current scope and invokes them sequentially.
/// Failures are logged but do not abort the dispatch chain - handlers are independent observers.
/// </summary>
public sealed class DomainEventDispatcher(
    IServiceProvider services,
    IBackgroundTaskQueue backgroundQueue,
    ILogger<DomainEventDispatcher> logger) : IDomainEventDispatcher
{
    public async Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var handlers = services.GetServices<IDomainEventHandler<TEvent>>();
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(domainEvent, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Domain event handler {Handler} failed for event {Event}",
                    handler.GetType().Name, typeof(TEvent).Name);
            }
        }
    }

    public async Task DispatchStrictAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // Propagates handler failures so the caller cannot report success when an orchestration
        // transition failed (ADR-039).
        foreach (var handler in services.GetServices<IDomainEventHandler<TEvent>>())
            await handler.HandleAsync(domainEvent, ct).ConfigureAwait(false);
    }

    public void Publish<TEvent>(TEvent domainEvent) where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        backgroundQueue.Enqueue(async (sp, ct) =>
        {
            var dispatcher = sp.GetRequiredService<IDomainEventDispatcher>();
            await dispatcher.DispatchAsync(domainEvent, ct).ConfigureAwait(false);
        });
    }
}
