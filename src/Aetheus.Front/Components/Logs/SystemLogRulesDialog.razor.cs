// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Logs;

/// <summary>The organization's exclusion rules of the system logs (R-28): add one (pre-filled when the dialog
/// opens from a log line), switch one on or off, delete one. Each change is saved at once; the page reloads its
/// grid when the dialog closes.</summary>
public partial class SystemLogRulesDialog : ComponentBase, IDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private ActiveOrganizationService Organizations { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The pattern the new rule starts with; empty when the dialog opens from the page's menu.</summary>
    [Parameter] public string InitialPattern { get; set; } = string.Empty;

    /// <summary>The category the new rule starts with.</summary>
    [Parameter] public string InitialCategory { get; set; } = string.Empty;

    private readonly SystemLogRuleModel _model = new();
    private List<SystemLogExclusionRuleDto> _rules = [];
    private int? _organizationId;
    private bool _loadFailed;
    private bool _saving;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        _model.Pattern = InitialPattern;
        _model.Category = InitialCategory;
        _organizationId = Organizations.Active?.Id;
        Organizations.Changed += OnOrganizationChanged;
        if (_organizationId is not null)
            await LoadAsync();
    }

    /// <summary>R-34: the organization chosen in the dialog itself (or switched elsewhere) loads its rules.</summary>
    private void OnOrganizationChanged() => _ = InvokeAsync(async () =>
    {
        _organizationId = Organizations.Active?.Id;
        _rules = [];
        if (_organizationId is not null)
            await LoadAsync();
        StateHasChanged();
    });

    public void Dispose() => Organizations.Changed -= OnOrganizationChanged;

    private async Task LoadAsync()
    {
        try
        {
            _rules = await Api.SystemLogRules.ListAsync(_organizationId!.Value);
            _loadFailed = false;
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
    }

    internal async Task AddAsync()
    {
        if (_organizationId is not { } organization || _saving) return;
        _saving = true;
        _error = null;
        try
        {
            var outcome = await Api.SystemLogRules.CreateAsync(
                organization,
                new SaveSystemLogExclusionRuleRequest { Pattern = _model.Pattern.Trim(), Category = _model.Category.Trim() });
            if (!outcome.IsSuccess)
            {
                _error = outcome.StatusCode == System.Net.HttpStatusCode.Conflict ? L["SystemLogRuleExists"].Value : L["LoadFailed"].Value;
                return;
            }

            _model.Pattern = string.Empty;
            _model.Category = string.Empty;
            Toast.Success("SystemLogRuleAdded");
            await LoadAsync();
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task SetEnabledAsync(SystemLogExclusionRuleDto rule, bool enabled)
    {
        if (_organizationId is not { } organization) return;
        var outcome = await Api.SystemLogRules.SetEnabledAsync(organization, rule.Id, enabled);
        if (outcome.IsSuccess)
            Toast.Success(enabled ? "SystemLogRuleEnabled" : "SystemLogRuleDisabled");
        await LoadAsync();
    }

    private async Task DeleteAsync(SystemLogExclusionRuleDto rule)
    {
        if (_organizationId is not { } organization) return;
        var status = await Api.SystemLogRules.DeleteAsync(organization, rule.Id);
        if (status.Success)
            Toast.Success("SystemLogRuleDeleted");
        await LoadAsync();
    }
}

public sealed class SystemLogRuleModel
{
    [Required]
    [StringLength(SaveSystemLogExclusionRuleRequest.MaxPatternLength)]
    public string Pattern { get; set; } = string.Empty;

    [StringLength(SaveSystemLogExclusionRuleRequest.MaxCategoryLength)]
    public string Category { get; set; } = string.Empty;
}
