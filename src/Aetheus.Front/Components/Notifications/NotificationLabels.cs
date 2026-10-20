// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Notifications;

/// <summary>
/// Text and badge colour for a notification's event type and delivery status, shared by the bell, the
/// /notifications page and the preferences. The status always carries its text label; the colour only
/// repeats it.
/// </summary>
public static class NotificationLabels
{
    /// <summary>The event's label, or the raw event type when no translation exists for it.</summary>
    public static string EventLabel(IStringLocalizer localizer, string eventType)
    {
        var localized = localizer[EventKey(eventType)];
        return localized.ResourceNotFound ? eventType : localized.Value;
    }

    /// <summary><c>pipeline.approval-requested</c> reads <c>NotificationEvent_pipeline_approval_requested</c>.</summary>
    public static string EventKey(string eventType) =>
        "NotificationEvent_" + eventType.Replace('.', '_').Replace('-', '_');

    /// <summary>The icon of one exact event type, read before the family icons below.</summary>
    private static readonly Dictionary<string, OmniIconName> ExactIcons = new(StringComparer.Ordinal)
    {
        [NotificationEventTypes.PipelineFailed] = OmniIconName.Error,
        [NotificationEventTypes.PipelineSucceeded] = OmniIconName.CheckCircle,
        [NotificationEventTypes.PipelineRejected] = OmniIconName.Prohibit,
        [NotificationEventTypes.PipelineApprovalRequested] = OmniIconName.Stamp,
        [NotificationEventTypes.PipelineLaunchRefused] = OmniIconName.Prohibit,
        [NotificationEventTypes.ReleaseDeployed] = OmniIconName.CloudArrowUp,
        [NotificationEventTypes.AgentUpdateCompleted] = OmniIconName.Upgrade,
        [NotificationEventTypes.AgentUpdateFailed] = OmniIconName.Error,
        [NotificationEventTypes.AiTaskCompleted] = OmniIconName.Sparkle,
        ["app.down"] = OmniIconName.WifiSlash,
        ["app.recovered"] = OmniIconName.WifiHigh,
        ["SecretExpiring"] = OmniIconName.Key
    };

    /// <summary>The icon of an event family by prefix, the most specific prefix first.</summary>
    private static readonly (string Prefix, OmniIconName Icon)[] FamilyIcons =
    [
        ("pipeline.", OmniIconName.RocketLaunch),
        ("analysis.gate.", OmniIconName.ShieldWarning),
        ("analysis.", OmniIconName.ShieldCheck),
        ("app.", OmniIconName.Monitor),
        ("alert.", OmniIconName.Warning),
        ("certbot.", OmniIconName.Lock)
    ];

    /// <summary>The one icon of a row in the bell, on its left, says what happened.</summary>
    public static OmniIconName EventIcon(string eventType)
    {
        if (ExactIcons.TryGetValue(eventType, out var icon)) return icon;
        foreach (var (prefix, familyIcon) in FamilyIcons)
        {
            if (eventType.StartsWith(prefix, StringComparison.Ordinal)) return familyIcon;
        }
        return OmniIconName.Bell;
    }

    public static string StatusLabel(IStringLocalizer localizer, NotificationDeliveryStatus status) =>
        localizer.Localize(status);

    /// <summary>Recette R3-16: an event the catalogue never e-mails (a finished AI task) has no e-mail
    /// outcome to show; its badge says it lives in the inbox, not that e-mail is not configured.</summary>
    public static bool IsInboxOnly(string eventType) =>
        NotificationEventTypes.Find(eventType) is { SentByEmail: false };

    public static string StatusLabel(IStringLocalizer localizer, NotificationDeliveryDto delivery) =>
        IsInboxOnly(delivery.EventType) ? localizer["NotificationInboxOnly"] : StatusLabel(localizer, delivery.Status);

    public static OmniTone StatusVariant(NotificationDeliveryDto delivery) =>
        IsInboxOnly(delivery.EventType) ? OmniTone.Neutral : StatusVariant(delivery.Status);

    /// <summary>Recette R-169: waiting, sent, failed and not configured each have their own shape.</summary>
    public static OmniIconName StatusIcon(NotificationDeliveryStatus status) => status switch
    {
        NotificationDeliveryStatus.Sent => OmniIconName.Check,
        NotificationDeliveryStatus.Failed => OmniIconName.Close,
        NotificationDeliveryStatus.NotConfigured => OmniIconName.Prohibit,
        _ => OmniIconName.Hourglass
    };

    public static string StatusIconClass(NotificationDeliveryStatus status) => status switch
    {
        NotificationDeliveryStatus.Sent => "notification-status-sent",
        NotificationDeliveryStatus.Failed => "notification-status-failed",
        NotificationDeliveryStatus.NotConfigured => "notification-status-not-configured",
        _ => "notification-status-pending"
    };

    public static OmniTone StatusVariant(NotificationDeliveryStatus status) => status switch
    {
        NotificationDeliveryStatus.Sent => OmniTone.Success,
        NotificationDeliveryStatus.Failed => OmniTone.Danger,
        NotificationDeliveryStatus.NotConfigured => OmniTone.Warning,
        _ => OmniTone.Neutral
    };
}
