// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Mail;

/// <summary>Closes with the trimmed folder name; the backend checks it again and creates
/// it.</summary>
public partial class MailboxFolderDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private readonly MailboxFolderModel _model = new();
    private bool _closed;

    internal void Submit()
    {
        if (_closed) return;
        _closed = true;
        Dialog.Close(_model.Name.Trim());
    }
}

public sealed class MailboxFolderModel
{
    [Required]
    [StringLength(MailboxLimits.MaxFolderNameLength)]
    [RegularExpression("^[^/\\\\.*%\"]+$")]
    public string Name { get; set; } = string.Empty;
}
