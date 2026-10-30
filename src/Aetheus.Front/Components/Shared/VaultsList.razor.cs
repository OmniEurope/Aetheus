// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// 0-a: shared Vaults list, reused across the 3 scopes. Global/project are server-paginated via
/// <c>GetVaultsAsync</c> (the endpoint supports a <c>projectId</c> filter); server detail is
/// self-loaded via <c>GetServerVaultsAsync</c> and paged in-memory (no server-side serverId paging),
/// with a <see cref="ListCacheService"/> entry for stale-while-revalidate. Self-loading - no
/// dependency on the detail loaders.
/// </summary>
public partial class VaultsList
{
    [Inject] private ListCacheService Cache { get; set; } = default!;

    /// <summary>Project filter (project detail scope). Mutually exclusive with <see cref="ServerId"/>.</summary>
    [Parameter] public int? ProjectId { get; set; }

    /// <summary>Server filter (server detail scope). When both are null the list is global (unfiltered).</summary>
    [Parameter] public int? ServerId { get; set; }

    private bool IsServerScope => ServerId.HasValue;
    private bool IsProjectScope => ProjectId.HasValue;
    private bool IsGlobal => !ProjectId.HasValue && !ServerId.HasValue;

    private List<VaultDto> _vaults = [];
    private List<VaultDto> _serverAll = []; // server scope: full list, paged client-side
    private int _totalCount;
    private bool _loading;
    private bool _canWrite;
    // The project column's checkable filter lists every project owning a vault the caller can read:
    // served by the API for the global list, taken from the loaded list in server scope.
    private VaultFilterValuesDto _filterValues = new();
    private Dictionary<int, List<ResourceUsageUserDto>> _usage = [];
    private bool _usageLoaded;

    private string CacheKey => $"vaults:server:{ServerId}";

    // Server-paginated (global/project) scope cache key, distinct from the server-detail CacheKey above.
    // The sort is part of the key: two orders sharing one entry means the second is served the first
    // one's rows, which is indistinguishable from a sort that does nothing.
    private string PagedCacheKey(int page, int pageSize, IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters,
        string? sortBy = null, bool sortDescending = false) =>
        $"vaults:{ProjectId}:{page}:{pageSize}:{GridColumnFilters.CacheText(filters)}:{sortBy}:{sortDescending}";

    private void ApplyVaultsPage(PaginatedResult<VaultDto> result)
    {
        _vaults = result.Items;
        _totalCount = result.TotalCount;
    }

    protected override async Task OnInitializedAsync()
    {
        FollowPermissions();
        RefreshCanWrite();

        if (IsServerScope && Cache.TryGet<List<VaultDto>>(CacheKey, out var cached) && cached is not null)
        {
            _serverAll = cached;
            _totalCount = cached.Count;
        }
        else if (!IsServerScope)
        {
            // Stale-while-revalidate: pre-seed the paginated view so the first paint is instant.
            Cache.Seed<PaginatedResult<VaultDto>>(PagedCacheKey(1, 20, null), ApplyVaultsPage);
        }

        if (IsServerScope)
        {
            try { await LoadServerVaultsAsync(); }
            catch (HttpRequestException) { } // 401 on expired JWT - redirect handled by AuthProvider
        }

        await LoadFilterValuesAsync();
        await LoadUsageAsync();
        // R-64: a pipeline created or changed afterwards changes who reads each vault.
        await FollowEntitiesAsync(ResourceType.Vault, ResourceType.Pipeline);
    }

    /// <summary>Who reads each vault (R-66). A failed call leaves the column empty rather than claiming
    /// every vault is unused.</summary>
    private async Task LoadUsageAsync()
    {
        try
        {
            _usage = (await Api.Variables.GetResourceUsageAsync()).Vaults;
            _usageLoaded = true;
            StateHasChanged();
        }
        catch (HttpRequestException) { } // the list stays usable without the usage column
    }

    private IReadOnlyList<ResourceUsageUserDto>? UsersOf(int vaultId) =>
        _usage.TryGetValue(vaultId, out var users) ? users : null;

    /// <summary>The detail page of a vault, remembering this list so its Back and its deletion return here.</summary>
    private string VaultHref(int id) => LocalReturnUrl.Append($"/vaults/{id}", LocalReturnUrl.Current(Nav));

    private async Task LoadFilterValuesAsync()
    {
        if (!IsGlobal) return;
        try { _filterValues = await Api.Variables.GetVaultFilterValuesAsync(); }
        catch (HttpRequestException) { } // the column still filters by typed text without the list
    }

    private async Task LoadServerVaultsAsync()
    {
        // FRONT-020: a list that arrives after a sign-out is neither cached nor shown.
        var ticket = Cache.Begin();
        var vaults = await Api.Servers.GetServerVaultsAsync(ServerId!.Value);
        if (!Cache.TryStore(ticket, CacheKey, vaults)) return;
        _serverAll = vaults;
        _totalCount = _serverAll.Count;
        _filterValues = new VaultFilterValuesDto
        {
            ProjectNames = [.. _serverAll.Select(item => item.ProjectName).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]
        };
    }

    protected override void OnPermissionsChanged()
    {
        RefreshCanWrite();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshCanWrite() => _canWrite = Permissions.CanWrite(ResourceType.Vault);

    /// <summary>One page of the export, the vaults as listed (names and counts, never a secret
    /// value), in the scope of the list, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<VaultDto>> ReadExportAsync(GridLoadArgs args)
    {
        if (IsServerScope)
        {
            return Task.FromResult(new PaginatedResult<VaultDto>
            {
                Items = args.ToClientPage(_serverAll),
                TotalCount = args.ClientFilteredCount(_serverAll)
            });
        }

        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(VaultDto.Name));
        return Api.Variables.GetVaultsAsync(page: page, pageSize: pageSize, projectId: ProjectId,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private async Task OnLoadData(GridLoadArgs args)
    {
        if (IsServerScope)
        {
            // Server-detail scope: page the in-memory list (already cached via LoadServerVaultsAsync).
            _vaults = args.ToClientPage(_serverAll);
            _totalCount = args.ClientFilteredCount(_serverAll);
            return;
        }

        // Every header filter goes to the API, which checks it against its column map.
        var filters = args.ToApiFilters();
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(VaultDto.Name));
        await Cache.RevalidateAsync(
            PagedCacheKey(page, pageSize, filters, sortBy, sortDescending),
            () => Api.Variables.GetVaultsAsync(page: page, pageSize: pageSize, projectId: ProjectId,
                sortBy: sortBy, sortDescending: sortDescending, filters: filters),
            ApplyVaultsPage,
            loading => _loading = loading,
            () => InvokeAsync(StateHasChanged));
    }

    private AetheusDataGrid<VaultDto>? _grid;

    /// <summary>A change pushed by the hub refreshes the grid quietly, its page, sort, filters and
    /// scroll kept, and brings a new project into the project filter.</summary>
    private async Task RefreshGridAsync()
    {
        await LoadFilterValuesAsync();
        if (_grid is not null) await _grid.Refresh();
    }

    private void NewVault() => Nav.NavigateTo(IsProjectScope
        ? $"/vaults/new?projectId={ProjectId}"
        : "/vaults/new");

    /// <summary>A vault or a pipeline changed, or the hub reconnected: the server scope reloads its list, the
    /// grid refreshes, and who reads each vault is asked again.</summary>
    protected override async Task OnEntitiesChangedAsync()
    {
        if (IsServerScope)
        {
            try { await LoadServerVaultsAsync(); } catch (HttpRequestException) { } // hub-triggered reload - silent on auth failure
        }
        await RefreshGridAsync();
        // Kept as is when the call fails, so a failure never turns into "Unused".
        await LoadUsageAsync();
    }
}
