// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers.Events;

/// <summary>New out-of-memory kills reported by a server's agent (PLAN-010 lot 1). <paramref
/// name="Victims"/> names the killed processes the agent could read, "process (pid N)" each, and
/// is empty when the kernel log was not readable; <paramref name="KillCount"/> is always the number
/// of new kills. <paramref name="Kills"/> holds the kind (container limit or whole host), the container and
/// the memory of each named victim when the agent reported them; null or empty for an older agent.</summary>
public sealed record OomKillDetectedEvent(
    int ServerId,
    string ServerName,
    long KillCount,
    IReadOnlyList<string> Victims,
    string Message,
    DateTime DetectedAt,
    IReadOnlyList<OomKillDetail>? Kills = null) : IDomainEvent;
