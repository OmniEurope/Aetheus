// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

public partial class RoleCreateDialog
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The organization the new role belongs to; null for a role of every organization (site Admin
    /// only, owner decision 2026-10-28).</summary>
    [Parameter] public int? OrganizationId { get; set; }

    private readonly RoleFormModel _model = new();
    private bool _saving;
    private string? _error;

    private async Task OnSubmit()
    {
        _saving = true;
        _error = null;

        try
        {
            var created = await Api.Auth.CreateRoleAsync(new CreateRoleRequest
            {
                Name = _model.Name,
                Description = _model.Description,
                OrganizationId = OrganizationId
            });

            if (created is not null)
            {
                Toast.Success("Created", "Saved");
                Dialog.Close(created);
                return;
            }

            _error = L["Error"].Value;
            Toast.Error("Error", "SaveFailed");
        }
        catch (HttpRequestException)
        {
            _error = L["Error"].Value;
            Toast.Error("Error", "SaveFailed");
        }
        _saving = false;
    }

    private void Cancel() => Dialog.Close(null);

}
