// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

/// <summary>The user detail tab listing the pipelines the user owns, with the bulk "Transfer to"
/// action.</summary>
public partial class UserOwnedPipelines
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public string Username { get; set; } = string.Empty;

    private List<PipelineOwnershipDto> _owned = [];
    private bool _loading = true;
    private string? _loadedFor;

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == Username) return;
        _loadedFor = Username;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _owned = await Api.Pipelines.GetOwnedPipelinesAsync(Username);
        }
        catch (HttpRequestException)
        {
            // 401 on an expired JWT: the redirect is handled by the auth provider.
            _owned = [];
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnTransferAsync()
    {
        var result = await Dialog.OpenAsync<PipelineOwnerTransferDialog>(
            L["TransferPipelinesTo"].Value,
            new Dictionary<string, object?>
            {
                [nameof(PipelineOwnerTransferDialog.Scope)] = new PipelineOwnerTransferRequest { CurrentOwnerUsername = Username },
                [nameof(PipelineOwnerTransferDialog.Description)] = string.Format(
                    L["TransferUserPipelinesDescription"].Value, _owned.Count, Username),
                [nameof(PipelineOwnerTransferDialog.DefaultOwner)] = Auth.Username == Username ? null : Auth.Username,
            },
            new OmniDialogOptions { Width = "560px", AutoFocusFirstElement = false });
        if (result is PipelineOwnerTransferResultDto)
        {
            await LoadAsync();
            StateHasChanged();
        }
    }
}
