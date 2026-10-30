// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public abstract class ServerActionSectionBase : ComponentBase
{
    [Inject] protected NotifyHelper Toast { get; set; } = default!;
    [Inject] protected IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] protected OmniDialogService Dialog { get; set; } = default!;
    [Inject] protected BusyVeil Veil { get; set; } = default!;

    [Parameter, EditorRequired] public ServerDetailDto Server { get; set; } = default!;

    protected bool _actionRunning;

    /// <summary>SITE-OFFLINE-001: the section's actions are disabled while the agent cannot claim a task.</summary>
    protected bool AgentOffline => AgentOfflineGuard.IsOffline(Server);

    /// <summary>SITE-OFFLINE-001: the handler guard behind the disabled buttons; true when the action was refused.</summary>
    protected bool BlockedByOfflineAgent() => AgentOfflineGuard.Blocks(Server, Toast);

    /// <summary>Recette R3-37: the section's <see cref="ServerTaskFollower"/>, when its markup places one;
    /// it then shows what becomes of each task <see cref="ExecuteServerActionAsync"/> queues.</summary>
    protected ServerTaskFollower? _follower;

    /// <param name="changesInventory">False for an action that only reads (a configuration test): the follower then
    /// does not wait for a new server state after it (R-119).</param>
    protected async Task ExecuteServerActionAsync(
        Func<Task<ApiStatus>> action, string? followTitle = null, bool changesInventory = true)
    {
        if (BlockedByOfflineAgent()) return;
        _actionRunning = true;
        if (followTitle is not null) _follower?.Expect(followTitle, settle: changesInventory);
        try
        {
            if ((await action()).Success) Toast.Success(L["TaskQueued"]);
            else
            {
                _follower?.Forget();
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch
        {
            _follower?.Forget();
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _actionRunning = false;
        }
    }

    /// <summary>An ordinary confirmation, whose blue confirm button says <paramref
    /// name="verb"/>.</summary>
    protected Task ShowConfirm(string title, string message, Func<Task> action, string verb) =>
        AskThenRunAsync(title, message, action, verb, destructive: false, icon: null);

    /// <summary>STD-BTN: a destructive confirmation, whose confirm button is red and says <paramref name="verb"/>.</summary>
    protected Task ShowDestructiveConfirm(string title, string message, Func<Task> action, string verb, OmniIconName? icon = null) =>
        AskThenRunAsync(title, message, action, verb, destructive: true, icon);

    // Every confirmation names its verb (never "Valider"), and "Revenir" dismisses it. The question
    // is the application's one confirmation dialog (STD-DIALOG), not a panel of this page.
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
        if (confirmed != true) return;
        if (VeilsConfirmedActionOnPage) await Veil.RunAsync(action);
        else await action();
    }

    /// <summary>R-56: false for a section whose confirmed actions all queue a task its <see cref="ServerTaskFollower"/>
    /// follows. The follower already veils the section body from the click until the task has ended; the page-wide
    /// <see cref="BusyVeil"/> would add a second veil, over the whole page, lifted as soon as the task is queued, so the
    /// veil would jump from the page to the section.</summary>
    protected virtual bool VeilsConfirmedActionOnPage => true;
}
