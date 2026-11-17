// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R-8 (option B): the editor of a server's daily reboot schedule, on the server's page. It reads the schedule
/// from <c>api/servers/{id}/reboot-schedule</c> and creates, changes, switches or deletes it there. A refusal of the
/// backend is said once by the global error handler; only a transport failure is said here. A write and a delete hold
/// the page veil while they run.
/// </summary>
public partial class ServerRebootScheduleCard
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private PermissionService Permissions { get; set; } = default!;
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>The server's name, said in the delete confirmation.</summary>
    [Parameter] public string ServerName { get; set; } = string.Empty;

    private ServerRebootScheduleDto? _schedule;
    private bool _loading = true;
    private bool _loadFailed;
    private bool _busy;

    /// <summary>The backend's own verdict once a schedule exists; before that, the caller's Admin right on the server.</summary>
    private bool CanManage => _schedule?.CanManage ?? Permissions.CanAdmin(ResourceType.Server, ServerId);

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        _loadFailed = false;
        try
        {
            _schedule = await Api.Servers.GetRebootScheduleAsync(ServerId);
        }
        catch (HttpRequestException)
        {
            _schedule = null;
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task EditAsync(ServerRebootScheduleDto? current)
    {
        var result = await Dialog.OpenAsync<ServerRebootScheduleDialog>(
            L[current is null ? "ServerRebootDialogCreateTitle" : "ServerRebootDialogEditTitle"],
            new Dictionary<string, object?> { ["Schedule"] = current },
            new OmniDialogOptions { Width = "28rem", CloseDialogOnOverlayClick = false, AutoFocusFirstElement = false });
        if (result is SaveServerRebootScheduleRequest request) await SaveAsync(request);
    }

    private Task ToggleAsync(ServerRebootScheduleDto current) => SaveAsync(new SaveServerRebootScheduleRequest
    {
        Hour = current.Hour,
        Minute = current.Minute,
        TimeZoneId = current.TimeZoneId,
        Enabled = !current.Enabled
    });

    private async Task SaveAsync(SaveServerRebootScheduleRequest request)
    {
        _busy = true;
        try
        {
            var saved = await Veil.RunAsync(() => Api.Servers.SaveRebootScheduleAsync(ServerId, request));
            if (saved is null) return;
            _schedule = saved;
            Toast.Success("ServerRebootCardTitle", "ServerRebootSavedDetail");
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "ServerRebootSaveFailed");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteAsync(ServerRebootScheduleDto current)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["ServerRebootDeleteConfirm"].Value, ServerName),
            L["ServerRebootDeleteAction"],
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"], CancelButtonText = L["GoBack"] });
        if (confirmed != true) return;

        _busy = true;
        try
        {
            var status = await Veil.RunAsync(() => Api.Servers.DeleteRebootScheduleAsync(ServerId));
            if (!status.Success && !status.NotFound) return;
            _schedule = null;
            Toast.Success("ServerRebootCardTitle", "ServerRebootDeletedDetail");
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "ServerRebootDeleteFailed");
        }
        finally
        {
            _busy = false;
        }
    }
}
