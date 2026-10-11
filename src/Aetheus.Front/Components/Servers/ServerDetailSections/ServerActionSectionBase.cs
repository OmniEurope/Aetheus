// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public abstract class ServerActionSectionBase : ComponentBase
{
    [Inject] protected NotifyHelper Toast { get; set; } = default!;
    [Inject] protected IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] protected OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;

    protected bool _actionRunning;

    /// <summary>SITE-OFFLINE-001: the section's actions are disabled while the agent cannot claim a task.</summary>
    protected bool AgentOffline => AgentOfflineGuard.IsOffline(Server);

    /// <summary>SITE-OFFLINE-001: the handler guard behind the disabled buttons; true when the action was refused.</summary>
    protected bool BlockedByOfflineAgent() => AgentOfflineGuard.Blocks(Server, Toast);

    protected async Task ExecuteServerActionAsync(Func<Task<ApiStatus>> action)
    {
        if (BlockedByOfflineAgent()) return;
        _actionRunning = true;
        try
        {
            if ((await action()).Success) Toast.Success(L["TaskQueued"]);
            else Toast.Error(L["ActionFailed"]);
        }
        catch
        {
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _actionRunning = false;
        }
    }

    /// <summary>An ordinary confirmation, whose blue confirm button says <paramref name="verb"/> (recette R-407).</summary>
    protected Task ShowConfirm(string title, string message, Func<Task> action, string verb) =>
        AskThenRunAsync(title, message, action, verb, destructive: false, icon: null);

    /// <summary>STD-BTN: a destructive confirmation, whose confirm button is red and says <paramref name="verb"/>.</summary>
    protected Task ShowDestructiveConfirm(string title, string message, Func<Task> action, string verb, OmniIconName? icon = null) =>
        AskThenRunAsync(title, message, action, verb, destructive: true, icon);

    // Recette R-407: every confirmation names its verb (never "Valider"), and "Revenir" dismisses it.
    // The question is the application's one confirmation dialog (STD-DIALOG), not a panel of this page.
    private async Task AskThenRunAsync(
        string title, string message, Func<Task> action, string verb, bool destructive, OmniIconName? icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        var confirmed = await Dialog.Confirm(message, title, new OmniConfirmOptions
        {
            Destructive = destructive,
            OkButtonText = verb,
            CancelButtonText = L["GoBack"].Value,
            ConfirmIcon = icon
        });
        if (confirmed == true)
            await action();
    }
}
