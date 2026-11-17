// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Settings;

public partial class AdminSettings : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    private AdminEntitySubscription? _adminRt;

    private List<AppSettingDto> _settings = [];
    // The secrets and the tokens APIs answer the whole list: the page holds it (read by the grid's first request), and
    // each grid's window (sort, column filters and page cut here, as the grid asks) is what is drawn. A list is
    // read again only when it is stale (first load, after a change, after a failed read), never for a sort, a filter or a scroll.
    private List<SecretDto> _secrets = [];
    private List<SecretDto> _secretRows = [];
    private int _secretCount;
    private bool _secretsStale = true;
    private List<RegistrationTokenDto> _tokens = [];
    private List<RegistrationTokenDto> _tokenRows = [];
    private int _tokenCount;
    private bool _tokensStale = true;
    private AetheusDataGrid<SecretDto>? _secretsGrid;
    private AetheusDataGrid<RegistrationTokenDto>? _tokensGrid;

    // The Used filter shows the same words as the cells, built once for a stable delegate.
    private Func<string, string>? _usedText;
    private Func<string, string> UsedText => _usedText ??= value =>
        bool.TryParse(value, out var used) ? (used ? L["Used"] : L["Available"]).Value : value;
    private Dictionary<string, string> _editBuffer = [];
    private readonly HashSet<string> _savingKeys = [];
    private readonly HashSet<int> _revealedTokens = [];
    private string _newSecretKey = string.Empty;
    private string _newSecretValue = string.Empty;
    private bool _allowRunToEnd;
    private bool _savingRunToEnd;

    // The "run to end" permission has its own switch below the free-form settings.
    private IEnumerable<AppSettingDto> TextSettings =>
        _settings.Where(setting => setting.Key != PipelineRunToEndSetting.Key);

    protected override async Task OnInitializedAsync()
    {
        if (!Auth.IsAdmin)
        {
            Nav.NavigateTo("/");
            return;
        }

        Breadcrumb.Set(
            new BreadcrumbItem(L["Administration"], "/admin"),
            new BreadcrumbItem(L["PlatformSettings"]));

        try
        {
            _settings = await Api.Settings.GetSettingsAsync();
            _editBuffer = _settings.ToDictionary(s => s.Key, s => s.Value);
            _allowRunToEnd = PipelineRunToEndSetting.IsAllowed(
                _settings.FirstOrDefault(s => s.Key == PipelineRunToEndSetting.Key)?.Value);
        }
        catch (HttpRequestException)
        {
            _settings = [];
        }

        await StartHubAsync();
    }

    // S-FEAT-RT2W / S-TECH-RT5K: refresh the registration-token list in realtime when a token is created
    // elsewhere or consumed by an enrolling agent. Uses the shared AdminEntitySubscription wrapper (the
    // 5th admin block to adopt it, after Users/Roles/Organizations/Plugins) instead of a bespoke hub.
    private Task StartHubAsync()
    {
        _adminRt = new AdminEntitySubscription(HubFactory);
        return _adminRt.StartAsync(AdminEntities.RegistrationToken, () => InvokeAsync(async () =>
        {
            try
            {
                _tokens = await Api.Auth.GetRegistrationTokensAsync();
                _tokensStale = false;
                // The grid cuts its window from the new list and marks the tokens it did not hold.
                if (_tokensGrid is not null) await _tokensGrid.Refresh();
            }
            catch (HttpRequestException) { } // hub-triggered reload - silent on auth failure
            StateHasChanged();
        }));
    }

    private async Task SaveSetting(string key)
    {
        if (_savingKeys.Contains(key)) return;
        if (!_editBuffer.TryGetValue(key, out var value)) return;

        _savingKeys.Add(key);
        try
        {
            var success = await Api.Settings.UpdateSettingAsync(key, value);
            if (success)
                Toast.Success("Saved", "SettingSaved", key);
            else
                Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _savingKeys.Remove(key);
        }
    }

    // Saved on change. A refused save puts the switch back where the server still has it.
    private async Task SaveRunToEndAsync(bool allowed)
    {
        if (_savingRunToEnd) return;
        _savingRunToEnd = true;
        try
        {
            var saved = await Api.Settings.UpdateSettingAsync(
                PipelineRunToEndSetting.Key, allowed ? "true" : "false");
            if (saved)
            {
                _allowRunToEnd = allowed;
                Toast.Success("Saved", "SettingSaved", L["AllowRunToEnd"].Value);
            }
            else
            {
                Toast.Error("Error", "SaveFailed");
            }
        }
        finally
        {
            _savingRunToEnd = false;
        }
    }

    private async Task AddSecret()
    {
        if (string.IsNullOrWhiteSpace(_newSecretKey) || string.IsNullOrWhiteSpace(_newSecretValue))
        {
            Toast.Warning("ValidationError", "SecretFieldsRequired");
            return;
        }

        var created = await Api.Settings.CreateSecretAsync(new CreateSecretRequest { Key = _newSecretKey, Value = _newSecretValue });
        if (created is not null)
        {
            // R-10: the secret shows as the server answered it, the quiet read behind it confirms.
            _secretsStale = true;
            if (_secretsGrid is not null) await _secretsGrid.ShowRowAsync(created);
            Toast.Success("Added", "SecretAdded");
            _newSecretKey = string.Empty;
            _newSecretValue = string.Empty;
        }
        else
        {
            Toast.Error("Error", "SaveFailed");
        }
    }

    private async Task DeleteSecret(SecretDto secret)
    {
        var confirmed = await Dialog.Confirm(L["DeleteSecretConfirm"].Value, L["DeleteSecret"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var success = await Api.Settings.DeleteSecretAsync(secret.Id);
            if (success)
            {
                // R-10: the secret leaves the grid at once, the quiet read behind it confirms.
                _secretsStale = true;
                if (_secretsGrid is not null) await _secretsGrid.HideRowAsync(secret);
                Toast.Success("Deleted", "SecretDeleted");
            }
            else
            {
                Toast.Error("Error", "DeleteFailed");
            }
        });
    }

    private async Task GenerateToken()
    {
        var token = await Api.Auth.CreateRegistrationTokenAsync();
        if (token is not null)
        {
            // R-10: the token shows as the server answered it, the quiet read behind it confirms.
            _tokensStale = true;
            if (_tokensGrid is not null) await _tokensGrid.ShowRowAsync(token);
            Toast.Success("Generated", "TokenGenerated");
        }
        else
        {
            Toast.Error("Error", "SaveFailed");
        }
    }

    private async Task LoadSecretsAsync(GridLoadArgs args)
    {
        if (_secretsStale)
        {
            _secrets = await Api.Settings.GetSecretsAsync();
            _secretsStale = false;
        }
        _secretRows = args.ToClientPage(_secrets);
        _secretCount = args.ClientFilteredCount(_secrets);
    }

    private async Task LoadTokensAsync(GridLoadArgs args)
    {
        if (_tokensStale)
        {
            _tokens = await Api.Auth.GetRegistrationTokensAsync();
            _tokensStale = false;
        }
        _tokenRows = args.ToClientPage(_tokens);
        _tokenCount = args.ClientFilteredCount(_tokens);
    }

    /// <summary>One page of the export (key and dates, the value is not in the list), cut from a fresh read with the grid's sort and column filters.</summary>
    private async Task<PaginatedResult<SecretDto>> ReadSecretsExportAsync(GridLoadArgs args)
    {
        var all = await Api.Settings.GetSecretsAsync();
        return new PaginatedResult<SecretDto> { Items = args.ToClientPage(all), TotalCount = args.ClientFilteredCount(all) };
    }

    private void ToggleTokenReveal(int tokenId)
    {
        if (!_revealedTokens.Remove(tokenId))
            _revealedTokens.Add(tokenId);
    }

    // CL8R: route token copy through the shared ClipboardService (one success toast + graceful
    // clipboard-unavailable handling) rather than a bespoke writeText + toast pair.
    private Task CopyToken(string token) => Clipboard.CopyAsync(token, L["TokenCopied"]);

    public async ValueTask DisposeAsync()
    {
        if (_adminRt is not null)
            await _adminRt.DisposeAsync();
    }
}
