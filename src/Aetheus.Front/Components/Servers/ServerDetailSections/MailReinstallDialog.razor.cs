// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>Typed confirmation of "Réinstaller la messagerie": closes with the typed word only when it is
/// <see cref="Word"/>; the backend checks it again.</summary>
public partial class MailReinstallDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The mail domain, or the mail host name when no domain is known.</summary>
    [Parameter, EditorRequired] public string Word { get; set; } = string.Empty;

    private readonly MailReinstallDialogModel _model = new();
    private bool _closed;

    internal bool Matches => Word.Length > 0
        && string.Equals(_model.Confirmation.Trim(), Word, StringComparison.OrdinalIgnoreCase);

    // Enter in the field submits the form too: only a matching word closes, and only once (a second
    // Close would close the dialog underneath).
    private void Submit()
    {
        if (!Matches || _closed) return;
        _closed = true;
        Dialog.Close(_model.Confirmation.Trim());
    }
}

public sealed class MailReinstallDialogModel
{
    [StringLength(253)]
    public string Confirmation { get; set; } = string.Empty;
}
