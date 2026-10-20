// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Notifications;

/// <summary>One catalogue entry of <see cref="NotificationEventTypes"/>.</summary>
/// <param name="SentByEmail">Recette R3-16: false for an event shown only in the inbox, never e-mailed.</param>
public sealed record NotificationEventTypeDescriptor(
    string EventType, bool DefaultEnabled, bool CarriesProjectId, bool SentByEmail = true);

/// <summary>A notification addressed to the current user.</summary>
public sealed record NotificationDeliveryDto
{
    public int Id { get; init; }
    public int? ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public NotificationDeliveryStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? SentAt { get; init; }
    public DateTime? ReadAt { get; init; }

    /// <summary>Read or not, as a column the /notifications grid can filter on.</summary>
    public bool IsRead => ReadAt is not null;
}

/// <summary>Query of <c>GET /api/notifications/me</c>.</summary>
public sealed record UserNotificationPageRequest
{
    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PaginationDefaults.DefaultPageSize;

    public NotificationDeliveryStatus? Status { get; init; }

    public bool UnreadOnly { get; init; }

    /// <summary>Only read (true) or only unread (false) deliveries; both when null.</summary>
    public bool? IsRead { get; init; }

    /// <summary>Part of the event type (<c>pipeline.failed</c>), case-insensitive.</summary>
    [StringLength(128)]
    public string? EventType { get; init; }

    /// <summary>Part of the subject, case-insensitive.</summary>
    [StringLength(200)]
    public string? Subject { get; init; }

    /// <summary>
    /// Column to order on, by <see cref="NotificationDeliveryDto"/> property name: CreatedAt (default),
    /// EventType, ChannelName, Subject or Status. An unknown name falls back to CreatedAt.
    /// </summary>
    [StringLength(32)]
    public string? SortBy { get; init; }

    /// <summary>Direction of <see cref="SortBy"/>; descending unless false.</summary>
    public bool SortDescending { get; init; } = true;

    /// <summary> The grid's header filters (status list, read yes/no, event list, subject, date
    /// range), applied next to the typed parameters above, which older callers still send.
    /// </summary>
    [MaxLength(PaginationRequest.MaxFilters)]
    public List<GridFilter>? Filters { get; init; }
}

/// <summary>The event types the /notifications grid's checkable Event filter offers.</summary>
public sealed record UserNotificationFilterValuesDto
{
    public List<string> EventTypes { get; init; } = [];
}

/// <summary>
/// The current user's choice for one event type. <see cref="IsSaved"/> is false when the value is the
/// catalogue default because the user never saved one.
/// </summary>
public sealed record NotificationPreferenceDto
{
    public string EventType { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public bool DefaultEnabled { get; init; }
    public bool IsSaved { get; init; }
    public bool CarriesProjectId { get; init; }
}

public sealed record NotificationPreferenceItem
{
    [Required]
    [StringLength(100)]
    public string EventType { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }
}

/// <summary>Body of <c>PUT /api/notifications/me/preferences</c>: the event types to save, others keep their value.</summary>
public sealed record UpdateNotificationPreferencesRequest
{
    [Required]
    [MaxLength(100)]
    public List<NotificationPreferenceItem> Preferences { get; init; } = [];
}

/// <summary>A project the current user follows: its events are notified to the user.</summary>
public sealed record ProjectSubscriptionDto
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
