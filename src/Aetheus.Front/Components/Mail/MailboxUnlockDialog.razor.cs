// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Mail;

/// <summary>
/// Recette R1-17: unlocks the mailbox; closes with the new <see cref="MailboxAccessDto"/> once the server accepts
/// the password. Recette R1-41 / R3-50: an administrator may open it without the password instead.
/// </summary>
public partial class MailboxUnlockDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public int AccountId { get; set; }

    /// <summary>Recette R1-41: the backend offers the opening without password (administrator, access prepared).</summary>
    [Parameter] public bool CanOpenAsAdministrator { get; set; }

    private readonly MailboxUnlockModel _model = new();
    private string? _error;
    private string? _adminError;
    private bool _busy;
    private bool _adminBusy;
    private bool _closed;

    internal async Task UnlockAsync()
    {
        if (_busy || _adminBusy || _closed) return;
        _busy = true;
        _error = null;
        try
        {
            var result = await Api.Mailbox.UnlockAsync(ServerId, AccountId, new MailboxUnlockRequest { Password = _model.Password });
            if (result is { Ok: true, Value: { } access })
            {
                _closed = true;
                Dialog.Close(access);
                return;
            }
            // A refused login: the field says so and the dialog stays open for another try.
            if (result.Locked) _error = L["MailboxUnlockRefused"];
        }
        finally
        {
            _busy = false;
        }
    }

    internal async Task OpenAsAdministratorAsync()
    {
        if (_busy || _adminBusy || _closed) return;
        _adminBusy = true;
        _adminError = null;
        try
        {
            var result = await Api.Mailbox.OpenAsAdministratorAsync(ServerId, AccountId);
            if (result is { Ok: true, Value: { } access })
            {
                _closed = true;
                Dialog.Close(access);
                return;
            }
            // Not prepared, refused by the server or no longer an administrator: say so, the password stays possible.
            _adminError = L["MailboxAdminOpenFailed"];
        }
        finally
        {
            _adminBusy = false;
        }
    }
}

public sealed class MailboxUnlockModel
{
    [Required]
    [StringLength(PasswordPolicy.MaximumLength)]
    public string Password { get; set; } = string.Empty;
}
