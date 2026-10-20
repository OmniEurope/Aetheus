// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ServerModulesSection
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    private List<ServerModuleDto>? _modules;
    private int? _loadedServerId;
    private bool _addVisible;
    private bool _addSaving;
    private AddModuleModel _addModel = new();
    private static readonly ServerModuleType[] _moduleTypes = Enum.GetValues<ServerModuleType>();

    // The Type and Status filters list the translated members, built once for stable delegates.
    private Func<string, string>? _typeText;
    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<ServerModuleType>(L);
    private Func<string, string>? _statusText;
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<ServerModuleStatus>(L);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedServerId == ServerId) return;
        _loadedServerId = ServerId;
        var serverId = ServerId;
        try
        {
            var modules = await Api.Servers.GetServerModulesAsync(serverId);
            if (ServerId == serverId) _modules = modules;
        }
        catch (HttpRequestException)
        {
            if (ServerId == serverId) _modules = [];
        } // 401 on expired JWT - redirect handled by AuthProvider
    }

    private async Task AddModuleAsync(AddModuleModel model)
    {
        _addSaving = true;
        try
        {
            var result = await Api.Servers.CreateServerModuleAsync(ServerId, new CreateServerModuleRequest
            {
                Name = model.Name,
                Type = model.Type,
                Version = string.IsNullOrWhiteSpace(model.Version) ? null : model.Version
            });
            if (result is null)
            {
                Toast.Error("Error", "CreateFailed");
                return;
            }

            _modules!.Add(result);
            _addVisible = false;
            _addModel = new AddModuleModel();
            Toast.Success("Created", "ModuleCreated");
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "CreateFailed");
        }
        finally
        {
            _addSaving = false;
        }
    }

    private sealed class AddModuleModel
    {
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;

        public ServerModuleType Type { get; set; }

        [StringLength(50)]
        public string Version { get; set; } = string.Empty;
    }

    private async Task DeleteModuleAsync(ServerModuleDto module)
    {
        var confirmed = await Dialog.Confirm(L["DeleteConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var success = await Api.Servers.DeleteServerModuleAsync(ServerId, module.Id);
        if (success)
        {
            _modules!.RemoveAll(m => m.Id == module.Id);
            Toast.Success("Deleted", "Deleted");
        }
        else
        {
            Toast.Error("Error", "DeleteFailed");
        }
    }

    private static OmniTone GetModuleStatusBadge(ServerModuleStatus status) => status switch
    {
        ServerModuleStatus.Active => OmniTone.Success,
        ServerModuleStatus.Inactive => OmniTone.Neutral,
        ServerModuleStatus.Error => OmniTone.Danger,
        _ => OmniTone.Warning
    };
}
