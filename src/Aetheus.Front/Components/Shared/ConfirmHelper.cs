// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>Use the shared OE confirmation with a red destructive action and neutral dismissal
/// under STD-BTN; only explicit confirmation returns true so dismissal cannot authorize
/// deletion.</summary>
public sealed class ConfirmHelper(AppDialogs dialogs, IStringLocalizer<AppStrings> localizer)
{
    public async Task<bool?> ConfirmDeleteAsync(string messageKey, string? titleKey = null, params object[] args) =>
        await dialogs.ConfirmAsync(
            localizer[titleKey ?? "ConfirmDelete"].Value,
            Format(messageKey, args),
            localizer["Delete"].Value,
            localizer["GoBack"].Value,
            destructive: true);

    /// <summary>Name the ordinary blue confirmation with the precise verb from confirmKey so the
    /// action is explicit.</summary>
    public async Task<bool?> ConfirmAsync(string confirmKey, string messageKey, string? titleKey = null, params object[] args) =>
        await dialogs.ConfirmAsync(
            localizer[titleKey ?? "Confirm"].Value,
            Format(messageKey, args),
            localizer[confirmKey].Value,
            localizer["GoBack"].Value);

    private string Format(string messageKey, object[] args) =>
        args.Length > 0
            ? string.Format(localizer[messageKey].Value, args)
            : localizer[messageKey].Value;
}
