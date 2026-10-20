// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Front.Components.Notifications;

public partial class InboxNotificationDialog
{
    [Parameter, EditorRequired] public NotificationDeliveryDto Notification { get; set; } = default!;

    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private string? Target => NotificationTargets.Path(Notification.EventType, Notification.PayloadJson);

    private void Open()
    {
        if (Target is not { } target) return;
        Dialog.Close();
        Nav.NavigateTo(target);
    }

    private IReadOnlyList<(string Name, string Value)> Elements { get; set; } = [];

    protected override void OnParametersSet() => Elements = PayloadElements(Notification.PayloadJson);

    /// <summary>
    /// The payload's top-level properties in their order, a nested value as its compact JSON. A payload
    /// that is not a JSON object (or not JSON at all) shows as one raw element rather than nothing.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> PayloadElements(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [(string.Empty, document.RootElement.GetRawText())];
            return document.RootElement.EnumerateObject()
                .Select(property => (property.Name, property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null => string.Empty,
                    _ => property.Value.GetRawText()
                }))
                .ToArray();
        }
        catch (JsonException)
        {
            return [(string.Empty, payloadJson)];
        }
    }
}
