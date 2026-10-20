// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Settings;

public partial class PersonalAccessTokens
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private List<PersonalAccessTokenDto> _tokens = [];
    private AetheusDataGrid<PersonalAccessTokenDto>? _grid;
    private int _totalCount;
    private int _page = 1;
    private int _pageSize = 25;
    private string? _sortBy;
    private bool _sortDescending = true;
    private bool _loading = true;
    private bool _creating;

    // The column header filters, each a real column filter of the endpoint.
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private Func<string, string>? _scopeText;
    private Func<string, string> ScopeText => _scopeText ??= GridFilterText.ForEnum<PatScope>(L);

    private readonly NewTokenModel _form = new();

    /// <summary>Plaintext token shown exactly once right after creation, then dismissed forever.</summary>
    private string? _revealedToken;

    private static readonly PatScope[] Scopes = [PatScope.ReadOnly, PatScope.ReadWrite];

    private sealed class NewTokenModel
    {
        [Required]
        [StringLength(64, MinimumLength = 1)]
        public string Name { get; set; } = string.Empty;

        public PatScope Scope { get; set; } = PatScope.ReadOnly;

        [Range(1, 90)]
        public int ExpirationDays { get; set; } = 30;
    }

    protected override Task OnInitializedAsync() => LoadPageAsync();

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        _pageSize = args.Top ?? 25;
        _page = ((args.Skip ?? 0) / _pageSize) + 1;
        (_sortBy, _sortDescending) = ResolveSort(args);
        _columnFilters = args.ToApiFilters();
        await LoadPageAsync();
    }

    /// <summary>One page of the export (the token's prefix, never the token), with the grid's sort
    /// and column filters.</summary>
    private Task<PaginatedResult<PersonalAccessTokenDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = ResolveSort(args);
        return Api.Auth.GetPersonalAccessTokensAsync(
            page, pageSize, sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private async Task LoadPageAsync()
    {
        _loading = true;
        try
        {
            var result = await Api.Auth.GetPersonalAccessTokensAsync(
                _page, _pageSize, sortBy: _sortBy, sortDescending: _sortDescending, filters: _columnFilters);
            _tokens = result.Items;
            _totalCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            _tokens = [];
            _totalCount = 0;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadAsync()
    {
        if (_grid is not null)
            await _grid.Reload();
        else
            await LoadPageAsync();
    }

    private static (string? SortBy, bool Descending) ResolveSort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return ("CreatedAt", true);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    private async Task CreateAsync()
    {
        _creating = true;
        try
        {
            var created = await Api.Auth.CreatePersonalAccessTokenAsync(new CreatePersonalAccessTokenRequest
            {
                Name = _form.Name,
                Scope = _form.Scope,
                ExpirationDays = _form.ExpirationDays
            });

            if (created is null)
            {
                Toast.Error("PatCreateFailed", "PatCreateFailed");
                return;
            }

            _revealedToken = created.PlaintextToken;
            _form.Name = string.Empty;
            _form.Scope = PatScope.ReadOnly;
            _form.ExpirationDays = 30;
            Toast.Success("PatCreated", "PatCreated");
            await LoadAsync();
        }
        finally
        {
            _creating = false;
        }
    }

    private Task CopyRevealedAsync()
        => _revealedToken is null ? Task.CompletedTask : Clipboard.CopyAsync(_revealedToken, L["CopiedToClipboard"]);

    private void DismissReveal() => _revealedToken = null;

    private async Task RevokeAsync(PersonalAccessTokenDto token)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["PatRevokeConfirm"].Value, token.Name), L["PatRevoke"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["PatRevoke"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        var status = await Api.Auth.RevokePersonalAccessTokenAsync(token.Id);
        if (status.Success)
        {
            Toast.Success("PatRevoked", "PatRevoked");
            await LoadAsync();
        }
        else
        {
            Toast.Error("PatRevokeFailed", "PatRevokeFailed");
        }
    }

    private string ScopeLabel(PatScope scope) =>
        scope == PatScope.ReadOnly ? L["PatScopeReadOnly"] : L["PatScopeReadWrite"];

    private OmniTone StatusStyle(PersonalAccessTokenDto t) =>
        t.RevokedAt is not null ? OmniTone.Neutral
        : t.ExpiresAt <= DateTime.Now ? OmniTone.Warning
        : OmniTone.Success;

    private string StatusLabel(PersonalAccessTokenDto t) =>
        t.RevokedAt is not null ? L["PatStatusRevoked"]
        : t.ExpiresAt <= DateTime.Now ? L["PatStatusExpired"]
        : L["PatStatusActive"];

    private static bool IsRevocable(PersonalAccessTokenDto t) => t.RevokedAt is null;

    /// <summary>The export writes the state the badge shows.</summary>
    private Func<PersonalAccessTokenDto, object?> StatusExport => _statusExport ??= token => StatusLabel(token);
    private Func<PersonalAccessTokenDto, object?>? _statusExport;
}
