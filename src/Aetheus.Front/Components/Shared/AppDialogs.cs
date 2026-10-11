// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// How the OmniEurope.Blazor UI opens a dialog (PLAN-008 lot 11, verification OE-13): a component
/// with its parameters, in a dialog of one of the <see cref="AppDialogWidth"/> widths, whose outcome
/// the component reports through <see cref="Close"/>. The dialog is shown by the host in
/// <c>App.razor</c>, which uses the same scoped <see cref="OmniOverlayService"/>.
/// </summary>
public sealed class AppDialogs(OmniOverlayService overlay, IStringLocalizer<AppStrings> localizer)
{
    /// <summary>
    /// Opens <typeparamref name="TComponent"/> with <paramref name="parameters"/> and waits for the
    /// dialog to close. Null when it is dismissed (close button, Escape, or the backdrop when
    /// <see cref="AppDialogOptions.CloseOnBackdropClick"/> allows it) rather than closed through
    /// <see cref="Close"/> with a result.
    /// </summary>
    public Task<object?> OpenAsync<TComponent>(
        string title,
        IDictionary<string, object?>? parameters = null,
        AppDialogOptions? options = null)
        where TComponent : IComponent
    {
        options ??= new AppDialogOptions();
        return overlay.OpenDialogAsync(new OmniDialogRequest(title, Content<TComponent>(parameters, options))
        {
            CloseOnBackdrop = options.CloseOnBackdropClick,
            Dismissible = options.Dismissible,
            ShowClose = options.ShowClose,

            Draggable = options.Draggable,
            Resizable = options.Resizable,
            Intent = options.Intent,
            // Recette R1-35: no field takes the focus when a dialog opens; the first Tab reaches it.
            InitialFocus = OmniDialogInitialFocus.Panel
        });
    }

    /// <summary>
    /// Same as <see cref="OpenAsync{TComponent}"/>, typed: the result when it is a
    /// <typeparamref name="TResult"/>, otherwise (dismissed, or closed with another type) the default.
    /// </summary>
    public async Task<TResult?> OpenAsync<TComponent, TResult>(
        string title,
        IDictionary<string, object?>? parameters = null,
        AppDialogOptions? options = null)
        where TComponent : IComponent =>
        await OpenAsync<TComponent>(title, parameters, options) is TResult result ? result : default;

    /// <summary>Closes the dialog on top and hands <paramref name="result"/> to whoever opened it.</summary>
    public void Close(object? result = null) => overlay.CloseDialog(result);

    /// <summary>
    /// Asks a yes-or-no question. True only when the action is chosen; cancelling and every way of
    /// dismissing the dialog answer false. STD-BTN (revised 2026-09-28, colour follows importance) and
    /// recette R-407 (decided 2026-09-28):
    /// <list type="bullet">
    /// <item>the confirm button names its short precise verb ("Créer", "Mettre à jour", "Supprimer"...):
    /// there is no generic "Valider", so <paramref name="confirmText"/> is required;</item>
    /// <item>an ordinary confirmation is the dialog's main action, blue (<see cref="OmniButtonVariant.Primary"/>,
    /// check icon); a <paramref name="destructive"/> one stays red (<see cref="OmniButtonVariant.Danger"/>)
    /// like the button that opened it;</item>
    /// <item>the dismiss button is the neutral grey (<see cref="OmniButtonVariant.Secondary"/>), labelled
    /// "Revenir" unless the caller names it.</item>
    /// </list>
    /// </summary>
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        bool destructive = false,
        OmniIconName? confirmIcon = null)
    {
        if (string.IsNullOrWhiteSpace(confirmText))
            throw new ArgumentException(
                "A confirmation names its verb (recette R-407, STD-BTN), never a generic \"Valider\".",
                nameof(confirmText));

        return overlay.ConfirmAsync(new OmniConfirmRequest(title, message)
        {
            ConfirmText = confirmText,
            CancelText = string.IsNullOrWhiteSpace(cancelText) ? localizer["GoBack"].Value : cancelText,
            ConfirmVariant = destructive ? OmniButtonVariant.Danger : OmniButtonVariant.Primary,
            CancelVariant = OmniButtonVariant.Secondary,
            ConfirmIcon = confirmIcon ?? (destructive ? OmniIconName.Delete : OmniIconName.Check)
        });
    }

    internal static string WidthClass(AppDialogWidth width) => width switch
    {
        AppDialogWidth.Narrow => "aetheus-dialog--narrow",
        AppDialogWidth.Wide => "aetheus-dialog--wide",
        AppDialogWidth.ExtraWide => "aetheus-dialog--extra-wide",
        _ => "aetheus-dialog--default"
    };

    private static RenderFragment Content<TComponent>(IDictionary<string, object?>? parameters, AppDialogOptions options)
        where TComponent : IComponent => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", string.Join(' ', new[]
        {
            "aetheus-dialog",
            WidthClass(options.Width),
            options.HeightClass,
            options.Resizable ? "aetheus-dialog--resizable" : null
        }.Where(value => value is not null)));
        builder.OpenComponent<TComponent>(2);
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
                builder.AddComponentParameter(3, name, value);
        }
        builder.CloseComponent();
        builder.CloseElement();
    };
}
