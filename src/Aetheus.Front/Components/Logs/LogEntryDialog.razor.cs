// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Logs;

public partial class LogEntryDialog : ComponentBase
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter] public string Message { get; set; } = string.Empty;
    [Parameter] public string? Exception { get; set; }

    /// <summary>The title of the second block; the exception details by default.</summary>
    [Parameter] public string? DetailsTitle { get; set; }

    /// <summary>Recette R3-35: the exact entry a log grid row was clicked on, in this dialog, opened the
    /// way the system log opens it.</summary>
    internal static Task OpenAsync(OmniDialogService dialog, IStringLocalizer<AppStrings> l,
        string message, string? details = null, string? detailsTitle = null) =>
        dialog.OpenAsync<LogEntryDialog>(l["Message"],
            new Dictionary<string, object?>
            {
                [nameof(Message)] = message,
                [nameof(Exception)] = details ?? string.Empty,
                [nameof(DetailsTitle)] = detailsTitle
            },
            new OmniDialogOptions { Width = "700px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });

    private Task CopyAsync()
    {
        var text = string.IsNullOrWhiteSpace(Exception) ? Message : $"{Message}\n\n{Exception}";
        return Clipboard.CopyAsync(text, L["MessageCopied"]);
    }

    private void Close() => Dialog.Close();
}
