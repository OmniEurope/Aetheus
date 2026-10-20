// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Servers.Handlers;

/// <summary>Admin toast payload; <c>Reachability</c> is the external probe result (PLAN-010 lot 1) so the
/// alert says whether the agent or the whole host stopped.</summary>
public record ServerOfflineNotificationPayload(
    int ServerId, string ServerName, DateTime? LastHeartbeat, ServerReachability Reachability = ServerReachability.NotProbed);
