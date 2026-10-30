// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Users;

public partial class RoleAssignmentPicker : IAsyncDisposable
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public List<string> SelectedRoles { get; set; } = [];
    [Parameter] public EventCallback<List<string>> SelectedRolesChanged { get; set; }
    [Parameter] public IReadOnlyList<string> InitialRoles { get; set; } = [];
    [Parameter] public bool Disabled { get; set; }

    /// <summary>
    /// R-47 (ADR-059): the roles other than the site-wide Admin, each held for one organization. When
    /// set, the picker shows the organization choice (hidden when there is only one) and toggles those
    /// roles here, while <see cref="SelectedRoles"/> keeps the site-wide Admin role only.
    /// </summary>
    [Parameter] public List<OrganizationRoleAssignmentDto>? SelectedOrganizationRoles { get; set; }
    [Parameter] public EventCallback<List<OrganizationRoleAssignmentDto>> SelectedOrganizationRolesChanged { get; set; }

    /// <summary>
    /// R-10 (d): set for an organization's administrator creating an account. The organization choice is these
    /// organizations only (hidden when there is one), <see cref="InitialOrganizationId"/> first, and the roles offered
    /// are those it gives there (the built-in ones but Admin, and the organization's own), read per organization.
    /// </summary>
    [Parameter] public IReadOnlyList<AdministeredOrganizationDto>? AdministeredOrganizations { get; set; }
    [Parameter] public int? InitialOrganizationId { get; set; }

    private sealed record OrganizationChoice(int Id, string Name);

    private List<OrganizationChoice> _organizations = [];
    private bool _organizationsLoaded;
    private int _organizationId;

    private bool OrganizationScoped => SelectedOrganizationRoles is not null;

    private List<string> SelectedLabels =>
        SelectedRoles
            .Concat((SelectedOrganizationRoles ?? []).Select(item => $"{item.Role} · {OrganizationName(item)}"))
            .ToList();

    private AetheusDataGrid<RoleDto>? _grid;
    private List<RoleDto> _roles = [];
    private int _totalCount;
    private int _page = 1;
    private int _pageSize = 25;
    private string? _sortBy = "Name";
    private bool _sortDescending;
    private bool _loading;
    private string? _search;
    private IReadOnlyList<GridFilterDescriptor>? _headerFilters;
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private bool _assignedOnly;
    private CancellationTokenSource? _searchDebounce;

    protected override async Task OnInitializedAsync()
    {
        if (!OrganizationScoped) return;
        if (AdministeredOrganizations is { } administered)
        {
            _organizations = [.. administered.Select(item => new OrganizationChoice(item.Id, item.Name))];
            _organizationId = _organizations.Any(item => item.Id == InitialOrganizationId)
                ? InitialOrganizationId!.Value
                : _organizations.FirstOrDefault()?.Id ?? 0;
            _organizationsLoaded = true;
            return;
        }
        try
        {
            var result = await Api.Servers.GetOrganizationsAsync(null, 1, 100, "Name");
            _organizations = [.. (result?.Items ?? []).Select(item => new OrganizationChoice(item.Id, item.Name))];
            _organizationId = _organizations.Count > 0 ? _organizations[0].Id : 0;
            _organizationsLoaded = result is not null;
        }
        catch (HttpRequestException)
        {
            // The organizations could not be read: roles held for one stay locked (no organization
            // chosen) and the "no organization exists" hint stays hidden, since that is not known.
            _organizations = [];
            _organizationId = 0;
        }
    }

    private async Task OnOrganizationChangedAsync(int organizationId)
    {
        _organizationId = organizationId;
        // An organization's administrator is offered that organization's own roles: the list changes with it.
        if (AdministeredOrganizations is not null)
            await ResetGridAsync();
        else if (_assignedOnly)
            await (_grid is not null ? _grid.Refresh() : LoadPageAsync());
    }

    private string OrganizationName(OrganizationRoleAssignmentDto assignment) =>
        _organizations.FirstOrDefault(item => item.Id == assignment.OrganizationId)?.Name
        ?? assignment.OrganizationName
        ?? assignment.OrganizationId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsSiteRole(string role) =>
        string.Equals(role, RoleNames.Admin, StringComparison.OrdinalIgnoreCase);

    // A role held for an organization needs one chosen; without any organization only Admin is open.
    private bool CanToggle(string role) => !OrganizationScoped || IsSiteRole(role) || _organizationId > 0;

    private async Task LoadDataAsync(GridLoadArgs args)
    {
        _pageSize = args.Top ?? 25;
        _page = ((args.Skip ?? 0) / _pageSize) + 1;
        (_sortBy, _sortDescending) = ResolveSort(args);
        // Send the Role header value and operator as a column filter to the roles API, distinct
        // from free-text search.
        _headerFilters = args.Filters;
        _columnFilters = args.ToApiFilters();
        await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        _loading = true;
        try
        {
            if (_assignedOnly || InitialRoles.Count > 0)
            {
                var source = _assignedOnly ? AssignedRoleNames() : InitialRoles;
                var selected = source
                    .Where(role => MatchesSearch(role, _search))
                    .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                    .Select(role => new RoleDto { Name = role })
                    .ToList();
                // The list is held here, so the header filter narrows it in memory, before the page is cut.
                var pageArgs = new GridLoadArgs { Skip = (_page - 1) * _pageSize, Top = _pageSize, Filters = _headerFilters };
                _totalCount = pageArgs.ClientFilteredCount(selected);
                _roles = pageArgs.ToClientPage(selected, _pageSize);
                return;
            }

            if (AdministeredOrganizations is not null && _organizationId <= 0)
            {
                (_roles, _totalCount) = ([], 0);
                return;
            }
            var result = AdministeredOrganizations is not null
                ? await Api.Auth.GetAssignableRoleDtosAsync(
                    _organizationId, _page, _pageSize, _search, _sortBy, _sortDescending, _columnFilters)
                : await Api.Auth.GetRoleDtosAsync(
                    _page, _pageSize, _search, _sortBy, _sortDescending, _columnFilters);
            _roles = result.Items;
            _totalCount = result.TotalCount;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnSearchInputAsync(string? value)
    {
        _search = value;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), _searchDebounce.Token);
            await InvokeAsync(ResetGridAsync);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke owns the pending search.
        }
    }

    private async Task ClearSearchAsync()
    {
        _searchDebounce?.Cancel();
        _search = null;
        await ResetGridAsync();
    }

    private async Task OnAssignedOnlyChanged(bool assignedOnly)
    {
        _assignedOnly = assignedOnly;
        await ResetGridAsync();
    }

    // The grid scrolls (remote virtualization) and only shows what it fetched itself, so a new
    // search or view reloads it from its first row through its own loader.
    private Task ResetGridAsync()
    {
        _page = 1;
        return _grid is not null ? _grid.GoToPage(0, forceReload: true) : LoadPageAsync();
    }

    private bool IsSelected(string role) =>
        OrganizationScoped && !IsSiteRole(role)
            ? SelectedOrganizationRoles!.Any(item => item.OrganizationId == _organizationId
                && string.Equals(item.Role, role, StringComparison.OrdinalIgnoreCase))
            : SelectedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

    // The roles ticked for the organization on screen, plus the site-wide ones.
    private IReadOnlyList<string> AssignedRoleNames() =>
        !OrganizationScoped
            ? SelectedRoles
            : SelectedRoles
                .Concat(SelectedOrganizationRoles!.Where(item => item.OrganizationId == _organizationId).Select(item => item.Role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static bool MatchesSearch(string value, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private async Task OnRoleToggledAsync(string role, bool selected)
    {
        if (OrganizationScoped && !IsSiteRole(role))
        {
            await OnOrganizationRoleToggledAsync(role, selected);
            return;
        }

        var updated = SelectedRoles.ToList();
        var existing = updated.FindIndex(item =>
            string.Equals(item, role, StringComparison.OrdinalIgnoreCase));

        if (selected && existing < 0)
            updated.Add(role);
        else if (!selected && existing >= 0)
            updated.RemoveAt(existing);

        SelectedRoles = updated;
        await SelectedRolesChanged.InvokeAsync(updated);

        if (_assignedOnly)
            await (_grid is not null ? _grid.Refresh() : LoadPageAsync());
    }

    private async Task OnOrganizationRoleToggledAsync(string role, bool selected)
    {
        if (_organizationId <= 0) return;
        var updated = SelectedOrganizationRoles!
            .Where(item => !(item.OrganizationId == _organizationId
                && string.Equals(item.Role, role, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (selected)
        {
            updated.Add(new OrganizationRoleAssignmentDto
            {
                OrganizationId = _organizationId,
                Role = role,
                OrganizationName = _organizations.FirstOrDefault(item => item.Id == _organizationId)?.Name
            });
        }

        SelectedOrganizationRoles = updated;
        await SelectedOrganizationRolesChanged.InvokeAsync(updated);

        if (_assignedOnly)
            await (_grid is not null ? _grid.Refresh() : LoadPageAsync());
    }

    private static (string? SortBy, bool Descending) ResolveSort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy))
            return ("Name", false);

        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1
            && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

    public ValueTask DisposeAsync()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        return ValueTask.CompletedTask;
    }
}
