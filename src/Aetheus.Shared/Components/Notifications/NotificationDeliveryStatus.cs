// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Notifications;

/// <summary>Persisted delivery-state integer values must remain stable. NotConfigured means no
/// sender or recipient address, while Sent requires actual transport acceptance.</summary>
public enum NotificationDeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    NotConfigured = 3
}
