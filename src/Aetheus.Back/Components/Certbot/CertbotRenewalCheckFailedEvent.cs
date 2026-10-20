// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Certbot;

/// <summary>Reports a failed renewal rehearsal received in a heartbeat. Servers owns heartbeat
/// collection; Certbot decides how to notify.</summary>
public sealed record CertbotRenewalCheckFailedEvent(int ServerId, DateTime CheckedAt) : IDomainEvent;
