// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Components.Vaults;

public partial class VaultEdit
{
    private DateOnly? NewSecretExpiryDate
    {
        get => _newSecret.ExpiresAt is { } value ? DateOnly.FromDateTime(value) : null;
        set => _newSecret.ExpiresAt = value?.ToDateTime(TimeOnly.MinValue);
    }
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    [Parameter] public int? Id { get; set; }

    [SupplyParameterFromQuery] public int? ProjectId { get; set; }
    [SupplyParameterFromQuery] public int? EnvironmentId { get; set; }
    [SupplyParameterFromQuery] public int? ProjectServerId { get; set; }

    /// <summary>The name a new vault starts with, when a page links here for one in particular.</summary>
    [SupplyParameterFromQuery] public string? Name { get; set; }

    /// <summary>The secret a link points at (R-10): the Key column of the grid starts filtered on it, a
    /// filter the reader sees and can remove.</summary>
    [SupplyParameterFromQuery(Name = "key")] public string? FilterKey { get; set; }

    /// <summary>The page that sent the user here to check what it created (the pipeline setup
    /// wizard's summary); Back returns to it when it is local.</summary>
    [SupplyParameterFromQuery(Name = LocalReturnUrl.Parameter)] public string? ReturnUrl { get; set; }

    private VaultDetailDto? _detail;
    private VaultModel _model = new();
    private List<ProjectDto> _projects = [];
    private bool _isNew => Id is null or 0;
    private bool _saving;
    private (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId)? _previousKey;
    private int _loadGeneration;

    private NewSecretModel _newSecret = new();

    protected override async Task OnParametersSetAsync()
    {
        var key = CurrentKey();
        if (key == _previousKey) return;
        _previousKey = key;
        var generation = ++_loadGeneration;
        var isNew = key.Id is null or 0;

        _detail = null;
        _model = new VaultModel();

        var projects = await Api.Projects.GetAllProjectsAsync();
        VaultDetailDto? detail = null;

        if (!isNew)
        {
            detail = await Api.Variables.GetVaultDetailAsync(key.Id!.Value);
        }
        if (!IsCurrentLoad(generation, key)) return;

        ApplyLoadedVault(key, isNew, projects, detail);
        if (!isNew) await LoadUsageAsync(key.Id!.Value, generation, key);
    }

    private IReadOnlyList<ResourceUsageUserDto>? _usage;
    private bool _usageLoaded;

    /// <summary>Who reads this vault (R-66). A failed call leaves the line empty rather than saying "Unused".</summary>
    private async Task LoadUsageAsync(int vaultId, int generation, (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId) key)
    {
        _usage = null;
        _usageLoaded = false;
        try
        {
            var usage = await Api.Variables.GetResourceUsageAsync();
            if (!IsCurrentLoad(generation, key)) return;
            _usage = usage.Vaults.GetValueOrDefault(vaultId);
            _usageLoaded = true;
        }
        catch (HttpRequestException) { } // the page stays usable without the usage line
    }

    private (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId) CurrentKey() =>
        (Id, ProjectId, EnvironmentId, ProjectServerId);

    /// <summary>False when the parameters changed, or a newer load started, while this one awaited.</summary>
    private bool IsCurrentLoad(int generation, (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId) key) =>
        generation == _loadGeneration && key == CurrentKey();

    private void ApplyLoadedVault(
        (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId) key,
        bool isNew,
        List<ProjectDto> projects,
        VaultDetailDto? detail)
    {
        _projects = projects;
        _detail = detail;
        if (detail is not null)
            _model = ModelFromDetail(detail);
        else if (isNew)
        {
            ApplyNewVaultOwner(_model, key, projects);
            // The readiness findings link here with the name of the missing vault (R2-22).
            if (!string.IsNullOrWhiteSpace(Name)) _model.Name = Name;
        }

        // Publish the parent project so the NavMenu keeps the project's submenu open
        // while we're editing one of its vaults.
        ProjectNav.Set(_model.ProjectId);

        ReassertBreadcrumb(isNew);
    }

    private static VaultModel ModelFromDetail(VaultDetailDto detail) => new()
    {
        Name = detail.Name,
        Description = detail.Description,
        ProjectId = detail.ProjectId,
        EnvironmentId = detail.EnvironmentId,
        ProjectServerId = detail.ProjectServerId
    };

    /// <summary>A new vault takes the owner named by the query: a known project first, otherwise an
    /// environment, otherwise a project server.</summary>
    private static void ApplyNewVaultOwner(
        VaultModel model,
        (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId) key,
        List<ProjectDto> projects)
    {
        if (key.ProjectId is > 0 && projects.Any(p => p.Id == key.ProjectId.Value))
            model.ProjectId = key.ProjectId;
        else if (key.EnvironmentId is > 0)
            model.EnvironmentId = key.EnvironmentId;
        else if (key.ProjectServerId is > 0)
            model.ProjectServerId = key.ProjectServerId;
    }

    private void ReassertBreadcrumb(bool isNew)
    {
        var current = new BreadcrumbItem(isNew ? L["NewVault"] : _detail?.Name ?? L["Vault"]);
        Breadcrumb.SetProjectResource(
            _model.ProjectId, _detail?.ProjectName, _projects,
            L["Projects"], L["Project"], L["Vaults"],
            "vaults", "/vaults", current);
    }

    private async Task OnSubmit()
    {
        _saving = true;
        if (_isNew)
        {
            var created = await Api.Variables.CreateVaultAsync(new CreateVaultRequest
            {
                Name = _model.Name,
                Description = _model.Description,
                ProjectId = _model.ProjectId,
                EnvironmentId = _model.EnvironmentId,
                ProjectServerId = _model.ProjectServerId
            });
            if (created is not null)
            {
                Toast.Success("Created", "VaultCreated");
                Nav.NavigateTo($"/vaults/{created.Id}");
            }
        }
        else
        {
            var updated = await Api.Variables.UpdateVaultAsync(Id!.Value, new UpdateVaultRequest
            {
                Name = _model.Name,
                Description = _model.Description,
                ProjectId = _model.ProjectId,
                EnvironmentId = _model.EnvironmentId,
                ProjectServerId = _model.ProjectServerId,
                RowVersion = _detail?.RowVersion ?? Guid.Empty
            });
            if (updated is not null)
            {
                Toast.Success("Saved", "VaultSaved");
                _editingProperties = false;
                await ReloadDetail();
            }
        }
        _saving = false;
    }

    private async Task OnDelete()
    {
        var confirmed = await Dialog.Confirm(L["DeleteVaultConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var status = await Api.Variables.DeleteVaultAsync(Id!.Value);
        if (!status.Success)
        {
            Toast.Error("Error", "DeleteFailed");
            return;
        }
        // S-TECH-SWIV: drop cached vault pages so the list doesn't briefly re-seed the deleted row.
        Cache.InvalidatePrefix("vaults:");
        Toast.Success("Deleted", "Deleted");
        // R-70: back to the list the user came from (a project's vaults, a server's), else the global list.
        Nav.NavigateTo(LocalReturnUrl.Validated(ReturnUrl) ?? "/vaults");
    }

    /// <summary>
    /// Fills the value field with a cryptographically secure value shaped by the typed key, so the
    /// well-known deployment secrets get exactly the recipe their host scripts would have produced.
    /// </summary>
    private void GenerateNewSecretValue()
    {
        _newSecret.Value = VaultSecretGenerator.Generate(_newSecret.Key);
        Toast.Success("Generated", VaultSecretGenerator.IsKnownKey(_newSecret.Key)
            ? "SecretValueGeneratedForKey"
            : "SecretValueGenerated");
    }

    /// <summary>One of the other ways to generate, from the Generate button's menu.</summary>
    private void GenerateNewSecretValueWith(SecretGenerationChoices.Choice choice)
    {
        _newSecret.Value = choice.Generate();
        Toast.Success("Generated", "SecretValueGenerated");
    }

    /// <summary>Tooltip naming the recipe that the generate button would apply to the current key.</summary>
    private string GenerateHint(string? key)
    {
        var profile = VaultSecretGenerator.ProfileFor(key);
        return profile.Key.Length > 0
            ? string.Format(L["GenerateSecretValueForKey"], profile.Key)
            : L["GenerateSecretValue"];
    }

    private async Task AddSecret(NewSecretModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Key) || string.IsNullOrWhiteSpace(model.Value))
            return;

        var secret = await Api.Variables.CreateVaultSecretAsync(Id!.Value, new CreateVaultSecretRequest
        {
            Key = model.Key,
            Value = model.Value,
            ExpiresAt = model.ExpiresAt
        });
        if (secret is not null)
        {
            _newSecret = new NewSecretModel();
            Toast.Success("Added", "SecretAdded");
            await ReloadDetail();
        }
    }

    private async Task OnSecretCellKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e, VaultSecretDto secret)
    {
        if (e.Key is "Enter" or " ")
            await UpdateSecret(secret);
    }

    private async Task UpdateSecret(VaultSecretDto secret)
    {
        var newValue = await Dialog.OpenAsync<SecretValueDialog>(
            string.Format(L["UpdateSecretValue"], secret.Key),
            new Dictionary<string, object?> { { "SecretKey", secret.Key } },
            new OmniDialogOptions { Width = "400px", AutoFocusFirstElement = false });

        if (newValue is string value && !string.IsNullOrEmpty(value))
        {
            var updated = await Api.Variables.UpdateVaultSecretAsync(Id!.Value, secret.Id, new UpdateVaultSecretRequest
            {
                Key = secret.Key,
                Value = value
            });
            // FRONT-025: a refused update (403, 400, 500) answers null; the old value is still in force.
            if (updated is null)
            {
                Toast.Error("Error", "SaveFailed");
                return;
            }
            Toast.Success("Saved", "SecretUpdated");
            await ReloadDetail();
        }
    }

    private async Task RotateSecret(VaultSecretDto secret)
    {
        // Rotation = same key, new value, audit-trailed as "Rotated" rather than "Updated".
        // Reuses SecretValueDialog because the input shape is identical.
        var confirmed = await Dialog.Confirm(
            string.Format(L["RotateSecretConfirm"], secret.Key),
            L["Rotate"].Value,
            new OmniConfirmOptions { OkButtonText = L["Rotate"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var newValue = await Dialog.OpenAsync<SecretValueDialog>(
            string.Format(L["RotateSecretValue"], secret.Key),
            new Dictionary<string, object?> { { "SecretKey", secret.Key } },
            new OmniDialogOptions { Width = "400px", AutoFocusFirstElement = false });

        if (newValue is string value && !string.IsNullOrEmpty(value))
        {
            var rotated = await Api.Variables.RotateVaultSecretAsync(Id!.Value, secret.Id, new RotateVaultSecretRequest
            {
                Value = value,
                ExpiresAt = secret.ExpiresAt
            });
            // FRONT-025: not rotated means the previous value is still the usable one; never say otherwise.
            if (rotated is null)
            {
                Toast.Error("Error", "SaveFailed");
                return;
            }
            Toast.Success("Rotated", "SecretRotated");
            await ReloadDetail();
        }
    }

    private async Task DeleteSecret(int secretId)
    {
        var confirmed = await Dialog.Confirm(L["DeleteSecretConfirm"].Value, L["DeleteSecret"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var status = await Api.Variables.DeleteVaultSecretAsync(Id!.Value, secretId);
        // FRONT-025: a refused delete leaves the secret in place.
        if (!status.Success)
        {
            Toast.Error("Error", status.Forbidden ? "AccessDenied" : "DeleteFailed");
            return;
        }
        Toast.Success("Deleted", "SecretDeleted");
        await ReloadDetail();
    }

    private async Task ShowVersions(VaultSecretDto secret)
    {
        var versions = await Api.Variables.GetVaultSecretVersionsAsync(Id!.Value, secret.Id);
        await Dialog.OpenAsync<SecretVersionHistoryDialog>(
            string.Format(L["VersionHistory"], secret.Key),
            new Dictionary<string, object?> { { "Versions", versions } },
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });
    }

    private async Task ExportKeys()
    {
        var keys = await Api.Variables.ExportVaultSecretKeysAsync(Id!.Value);
        var json = JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true });
        await JS.InvokeVoidAsync("downloadFile", $"{_detail?.Name ?? "vault"}-keys.json", json, "application/json");
        Toast.Success("Exported", "KeysExported", keys.Count);
    }

    private async Task ImportSecrets()
    {
        var json = await Dialog.OpenAsync<ImportJsonDialog>(
            L["ImportSecrets"].Value,
            new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });

        if (json is not string content || string.IsNullOrWhiteSpace(content)) return;

        List<CreateVaultSecretRequest>? secrets;
        try
        {
            secrets = JsonSerializer.Deserialize<List<CreateVaultSecretRequest>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            Toast.Error("Error", "InvalidJsonFormat");
            return;
        }

        if (secrets is null or { Count: 0 }) return;

        var result = await Api.Variables.ImportVaultSecretsAsync(Id!.Value, secrets);
        if (result is not null)
        {
            Toast.Success("Imported", "SecretsImported", result.ImportedCount);
            await ReloadDetail();
        }
    }

    /// <summary>Shows the vault properties form from the header menu.</summary>
    private bool _editingProperties;

    private void EditProperties() => _editingProperties = true;

    private void CancelPropertiesEdit()
    {
        _editingProperties = false;
        if (_detail is not null)
        {
            _model = new VaultModel
            {
                Name = _detail.Name,
                Description = _detail.Description,
                ProjectId = _detail.ProjectId,
                EnvironmentId = _detail.EnvironmentId,
                ProjectServerId = _detail.ProjectServerId
            };
        }
    }

    /// <summary>The key itself; the $(KEY) reference keeps its own button.</summary>
    private Task CopyKey(string key) => Clipboard.CopyAsync(key);

    /// <summary> The value goes from the server straight to the clipboard; it is never kept in a
    /// field, never rendered, and the server audits each read. </summary>
    private async Task CopySecretValue(VaultSecretDto secret)
    {
        RevealedSecretValueDto? revealed;
        try
        {
            revealed = await Api.Variables.RevealVaultSecretValueAsync(Id!.Value, secret.Id);
        }
        catch (HttpRequestException)
        {
            revealed = null;
        }
        if (revealed is null)
        {
            Toast.Error("Error", "OperationFailed");
            return;
        }
        await Clipboard.CopyAsync(revealed.Value);
    }

    private async Task CopyKeyReference(string key)
    {
        await Clipboard.CopyAsync($"$({key})", string.Format(L["KeyReferenceCopied"], key));
    }

    private async Task ReloadDetail()
    {
        _detail = await Api.Variables.GetVaultDetailAsync(Id!.Value);
    }

    private static OmniTone GetExpiryBadge(DateTime expiresAt)
    {
        var daysLeft = (expiresAt - DateTime.Now).TotalDays;
        if (daysLeft <= 0) return OmniTone.Danger;
        if (daysLeft <= 14) return OmniTone.Warning;
        return OmniTone.Neutral;
    }

    private sealed class NewSecretModel
    {
        [Required, StringLength(200)]
        public string Key { get; set; } = string.Empty;

        [Required, StringLength(KeyValueRequest.MaxValueLength)]
        public string Value { get; set; } = string.Empty;

        public DateTime? ExpiresAt { get; set; }
    }

    private sealed class VaultModel : ScopedResourceFormModel
    {
    }

    /// <summary>The export writes the count the badge shows (never the value).</summary>
    private static readonly Func<VaultSecretDto, object?> VersionsExport = secret => secret.VersionCount;
}
