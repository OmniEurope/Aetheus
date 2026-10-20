// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Notifications;

/// <summary>The column header filters of the notification channels and rules grids of the
/// administration page; each key is the grid column's key.</summary>
internal static class NotificationAdminListQuery
{
    internal static readonly GridQueryMap<NotificationChannel> ChannelColumns = new GridQueryMap<NotificationChannel>()
        .Text("name", channel => channel.Name)
        .Enum("type", channel => channel.Type)
        .Boolean("isEnabled", channel => channel.IsEnabled)
        .Number("ruleCount", channel => channel.Rules.Count);

    internal static readonly GridQueryMap<NotificationRule> RuleColumns = new GridQueryMap<NotificationRule>()
        .Text("eventType", rule => rule.EventType)
        .Text("channelName", rule => rule.Channel.Name)
        .Boolean("isEnabled", rule => rule.IsEnabled);
}
