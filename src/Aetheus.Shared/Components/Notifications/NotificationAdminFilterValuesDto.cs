// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Notifications;

/// <summary>The values the notification rules grid's checkable column filters offer.</summary>
public sealed record NotificationAdminFilterValuesDto
{
    public List<string> EventTypes { get; init; } = [];
    public List<string> Channels { get; init; } = [];
}
