// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>How a dialog opened through <see cref="AppDialogs"/> is laid out and dismissed.</summary>
public sealed record AppDialogOptions
{
    public AppDialogWidth Width { get; init; } = AppDialogWidth.Default;

    /// <summary>Keep backdrop dismissal disabled by default so a click beside a form does not
    /// discard input; the close button and Escape remain available.</summary>
    public bool CloseOnBackdropClick { get; init; }

    /// <summary>
    /// Whether the reader can dismiss the dialog. True by default. False removes the close button and
    /// ignores Escape and the backdrop: the dialog closes only when its content calls
    /// <see cref="AppDialogs.Close"/> (a non-dismissible dialog).
    /// </summary>
    public bool Dismissible { get; init; } = true;

    public bool ShowClose { get; init; } = true;

    public bool Draggable { get; init; }

    public bool Resizable { get; init; }

    /// <summary>Supply the dialog's intent independently of its neutral header and footer so
    /// purpose is visible without styling the whole frame; component dialogs default to Accent and
    /// confirmations derive intent from their action.</summary>
    public OmniTone Intent { get; init; } = OmniTone.Accent;

    internal string? HeightClass { get; init; }
}
