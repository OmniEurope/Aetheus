// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Notifications;

/// <summary> The column filters of the /notifications grid. "Read" is a yes/no column over the read
/// date, the status a list of delivery states, the date a range. </summary>
internal static class UserNotificationListQuery
{
    internal static readonly GridQueryMap<NotificationDelivery> Columns = new GridQueryMap<NotificationDelivery>()
        .Date("createdAt", d => d.CreatedAt)
        .Boolean("isRead", d => d.ReadAt != null)
        .Text("eventType", d => d.EventType)
        .Text("subject", d => d.Subject)
        .Enum("status", d => d.Status);
}
