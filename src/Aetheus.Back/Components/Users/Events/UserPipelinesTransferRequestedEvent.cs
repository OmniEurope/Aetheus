// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Users.Events;

/// <summary>Requests a strict ownership transfer from <paramref name="FromUsername"/> to <paramref
/// name="ToUsername"/> before deactivation or deletion, allowing Pipelines to handle it without an
/// upward Users dependency. The emitter verifies that no owned pipelines remain before continuing
/// (ADR-039).</summary>
public sealed record UserPipelinesTransferRequestedEvent(string FromUsername, string ToUsername) : IDomainEvent;
