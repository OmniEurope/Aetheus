// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

public class OmniDialogService(AppDialogs dialogs, OmniOverlayService overlay)
{
    private static readonly (string Fragment, AppDialogWidth Width)[] KnownWidths =
    [
        ("90vw", AppDialogWidth.ExtraWide),
        ("900px", AppDialogWidth.ExtraWide),
        ("920px", AppDialogWidth.ExtraWide),
        ("52rem", AppDialogWidth.Wide),
        ("40rem", AppDialogWidth.Default)
    ];

    public event Action<string?, Type, Dictionary<string, object?>?, OmniDialogOptions?>? OnOpen;

    public virtual Task<object?> OpenAsync<TComponent>(
        string title,
        Dictionary<string, object?>? parameters = null,
        OmniDialogOptions? options = null)
        where TComponent : IComponent
    {
        OnOpen?.Invoke(title, typeof(TComponent), parameters, options);
        return dialogs.OpenAsync<TComponent>(title, parameters, ToAppOptions(options));
    }

    public virtual async Task<bool?> Confirm(string? message, string? title, OmniConfirmOptions? options = null) =>
        await dialogs.ConfirmAsync(
            title ?? string.Empty,
            message ?? string.Empty,
            options?.OkButtonText,
            options?.CancelButtonText,
            options?.Destructive ?? false,
            options?.ConfirmIcon);

    public virtual async Task Alert(string message, string title, OmniAlertOptions? options = null)
    {
        var request = new OmniDialogRequest(title, builder =>
        {
            builder.OpenElement(0, "p");
            builder.AddContent(1, message);
            builder.CloseElement();
        })
        {
            // Every dialog carries an intention; a message to read is the accent one.
            Intent = OmniTone.Accent,
            // Nothing looks selected when the message opens.
            InitialFocus = OmniDialogInitialFocus.Panel,
            Footer = builder =>
            {
                builder.OpenComponent<OmniButton>(0);
                builder.AddComponentParameter(1, nameof(OmniButton.Variant), OmniButtonVariant.Secondary);
                builder.AddComponentParameter(2, nameof(OmniButton.OnClick),
                    EventCallback.Factory.Create<MouseEventArgs>(this, () => overlay.CloseDialog(true)));
                builder.AddComponentParameter(3, nameof(OmniButton.ChildContent),
                    (RenderFragment)(content => content.AddContent(0, options?.OkButtonText ?? "OK")));
                builder.CloseComponent();
            }
        };
        await overlay.OpenDialogAsync(request);
    }

    public virtual void Close(object? result = null) => dialogs.Close(result);

    private static AppDialogOptions ToAppOptions(OmniDialogOptions? options) => new()
    {
        Width = WidthOf(options?.Width),
        HeightClass = HeightClassOf(options?.Height),
        CloseOnBackdropClick = options?.CloseDialogOnOverlayClick ?? false,
        Dismissible = (options?.ShowClose ?? true)
            || (options?.CloseDialogOnEsc ?? true)
            || (options?.CloseDialogOnOverlayClick ?? false),
        ShowClose = options?.ShowClose ?? true,

        Draggable = options?.Draggable ?? false,
        Resizable = options?.Resizable ?? false
    };

    private static string? HeightClassOf(string? height) => height?.Trim().ToLowerInvariant() switch
    {
        "600px" => "aetheus-dialog--height-600",
        "720px" => "aetheus-dialog--height-720",
        "80vh" => "aetheus-dialog--height-80vh",
        "85vh" => "aetheus-dialog--height-85vh",
        _ => null
    };

    private static AppDialogWidth WidthOf(string? width)
    {
        if (string.IsNullOrWhiteSpace(width)) return AppDialogWidth.Default;
        var normalized = width.Trim().ToLowerInvariant();
        foreach (var knownWidth in KnownWidths)
        {
            if (normalized.Contains(knownWidth.Fragment, StringComparison.Ordinal)) return knownWidth.Width;
        }

        if (!TryParsePixels(normalized, out var value))
            return AppDialogWidth.Wide;

        return value switch
        {
            <= 450 => AppDialogWidth.Narrow,
            >= 800 => AppDialogWidth.ExtraWide,
            >= 600 => AppDialogWidth.Wide,
            _ => AppDialogWidth.Default
        };
    }

    private static bool TryParsePixels(string value, out double pixels)
    {
        var unit = value.EndsWith("rem", StringComparison.Ordinal) ? "rem"
            : value.EndsWith("px", StringComparison.Ordinal) ? "px"
            : null;
        if (unit is null
            || !double.TryParse(value[..^unit.Length], NumberStyles.Number, CultureInfo.InvariantCulture, out pixels))
        {
            pixels = 0;
            return false;
        }

        if (unit == "rem") pixels *= 16;
        return true;
    }
}

public sealed record OmniDialogOptions
{
    public string? Width { get; init; }
    public string? Height { get; init; }
    public bool CloseDialogOnOverlayClick { get; init; }
    /// <summary>Keep this parameter for call-site compatibility but do not forward it; focus the
    /// dialog panel so opening a message or form does not select a field.</summary>
    public bool AutoFocusFirstElement { get; init; }
    public bool CloseDialogOnEsc { get; init; } = true;
    public bool ShowClose { get; init; } = true;
    public bool Draggable { get; init; }
    public bool Resizable { get; init; }
}

public sealed record OmniConfirmOptions
{
    public string? OkButtonText { get; init; }
    public string? CancelButtonText { get; init; }

    /// <summary>STD-BTN: a destructive confirmation is red and names its verb in <see cref="OkButtonText"/>.</summary>
    public bool Destructive { get; init; }

    /// <summary>The confirm icon; defaults to delete for a destructive confirmation, check otherwise.</summary>
    public OmniIconName? ConfirmIcon { get; init; }
}

public sealed record OmniAlertOptions
{
    public string? OkButtonText { get; init; }
}
