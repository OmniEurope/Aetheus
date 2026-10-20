// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Notifications.Events;

/// <summary>Announces product notifications for higher modules to react independently (ADR-039).
/// Observer dispatch prevents subscriber failures from interrupting notification
/// delivery.</summary> <param name="EventType">Product event name the notification rules are keyed
/// on.</param> <param name="Payload">Event payload, serialized by whichever subscriber needs
/// it.</param>
public sealed record NotificationEventRaisedEvent(string EventType, object Payload) : IDomainEvent;
