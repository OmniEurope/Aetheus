// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.DomainEvents;

public interface IDomainEventDispatcher
{
    /// <summary>
    /// Synchronous in-process dispatch: awaits all registered handlers in the caller's scope.
    /// Use for events whose side-effects must complete before the caller continues
    /// (e.g. a release row created in response to a successful pipeline run).
    /// </summary>
    Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : IDomainEvent;

    /// <summary>Dispatches synchronously and propagates handler failures when the emitter owns the
    /// outcome, stopping subsequent handlers (ADR-039). <see cref="DispatchAsync{TEvent}"/> instead
    /// treats handlers as independent observers whose failures are logged.</summary>
    Task DispatchStrictAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : IDomainEvent;

    /// <summary>
    /// Fire-and-forget: enqueues handler dispatch on <see cref="IBackgroundTaskQueue"/>
    /// so the emitting transaction returns immediately. Use for non-critical observers
    /// (audit, notifications, metrics) where eventual consistency is acceptable.
    /// </summary>
    void Publish<TEvent>(TEvent domainEvent) where TEvent : IDomainEvent;
}
