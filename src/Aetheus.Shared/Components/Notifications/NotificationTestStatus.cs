// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Notifications;

/// <summary>
/// Outcome of sending a real test message through a notification channel. A channel with no
/// working transport (e.g. Email with no usable sender) reports <see cref="NotConfigured"/> - it is never
/// reported as <see cref="Sent"/> when nothing actually left the server.
/// </summary>
public enum NotificationTestStatus
{
    Sent = 0,
    NotConfigured = 1,
    Failed = 2
}
