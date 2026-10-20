// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers.Events;

/// <summary>Carries heartbeat mail inventory from Servers to Mail for reconciliation without a
/// reverse module dependency. Background dispatch prevents reconciliation failure from failing the
/// heartbeat (ADR-039).</summary>
public sealed record MailInventoryReportedEvent(int ServerId, MailDataDto Mail) : IDomainEvent;
