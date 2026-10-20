// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R3-49: the Accounts tab's menu (client settings, purge of absent mailboxes) and, for an
/// administrator, the preparation of the administrator access of recette R1-41 / R3-50.
/// </summary>
public partial class ServerMailAccountsMenu
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>The preparation queues a mail-manage task: the agent must be online with the mail capability.</summary>
    [Parameter] public bool CanManage { get; set; }

    /// <summary>Raised once rows were deleted, so the grid reloads.</summary>
    [Parameter] public EventCallback OnAccountsChanged { get; set; }

    private bool _busy;

    internal bool IsAdministrator => Auth.IsAdmin;

    internal Task OpenClientHelpAsync() => Dialog.OpenAsync<Aetheus.Front.Components.Mail.MailClientSettingsHelp>(L["MailClientHelpTitle"],
        new Dictionary<string, object?> { ["ServerId"] = ServerId, ["InDialog"] = true },
        new OmniDialogOptions { Width = "40rem", AutoFocusFirstElement = false });

    internal async Task PurgeMissingAsync()
    {
        if (_busy) return;
        var confirmed = await Dialog.Confirm(L["MailAccountsPurgeConfirm"].Value, L["MailAccountsPurge"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["MailAccountsPurgeVerb"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;
        _busy = true;
        try
        {
            var result = await Api.Mail.PurgeMissingMailAccountsAsync(ServerId);
            if (result is null)
            {
                Toast.Error(L["ActionFailed"]);
                return;
            }
            Toast.Success(string.Format(L["MailAccountsPurged"], result.Removed));
            if (result.Removed > 0) await OnAccountsChanged.InvokeAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    internal async Task PrepareMasterAccessAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var queued = await Api.Mail.PrepareMasterAccessAsync(ServerId);
            if (queued is null) Toast.Error(L["MailMasterAccessPrepareFailed"]);
            else Toast.Success(L["MailMasterAccessPrepareQueued"]);
        }
        finally
        {
            _busy = false;
        }
    }
}
