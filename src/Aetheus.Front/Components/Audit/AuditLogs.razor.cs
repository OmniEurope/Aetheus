// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Audit;

public partial class AuditLogs : IAsyncDisposable
{
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    private AetheusDataGrid<AuditLogDto>? _grid;
    private AdminEntitySubscription? _liveEntries;
    private readonly TrailingReloadCoalescer _liveReload = new(1000);

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private AuthStateProvider Auth { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private NotifyHelper Notify { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;

    private List<AuditLogDto> _logs = [];
    private int _count;
    private bool _loading;
    // The column header filters, and only they, filter the log (the timestamp range included).
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private Func<string, string>? _actionText;
    private List<string> _actionOptions = [];
    private List<string> _entityOptions = [];
    private AuditChainVerificationResult? _chainResult;
    private bool _chainChecked;
    private bool _verifyingChain;

    protected override async Task OnInitializedAsync()
    {
        if (!Auth.IsAdmin)
        {
            Nav.NavigateTo("/");
            return;
        }
        Breadcrumb.Set(new BreadcrumbItem(L["Administration"], "/admin"), new BreadcrumbItem(L["AuditLogs"]));

        try
        {
            var actionsTask = Api.Monitoring.GetAuditActionsAsync();
            var typesTask = Api.Monitoring.GetAuditEntityTypesAsync();
            await Task.WhenAll(actionsTask, typesTask);
            _actionOptions = await actionsTask;
            _entityOptions = await typesTask;
            // Stale-while-revalidate: pre-seed from the cached default view (grid PageSize = 20) so the
            // first paint is instant; LoadData then revalidates in the background.
            Cache.Seed<PaginatedResult<AuditLogDto>>(CacheKey(1, 20), ApplyLogs);
            await LoadData(new GridLoadArgs());
            _liveEntries = new AdminEntitySubscription(HubFactory);
            await _liveEntries.StartAsync(AdminEntities.AuditLog,
                () => InvokeAsync(() => _liveReload.RequestAsync(RefreshData)));
        }
        catch (HttpRequestException)
        {
            _actionOptions = [];
            _entityOptions = [];
        }
    }

    // The sort is part of the key: two orders sharing one entry means the second is served the first
    // one's rows, which is indistinguishable from a sort that does nothing.
    private string CacheKey(int page, int pageSize, string? sortBy = null, bool sortDescending = true) =>
        $"audit:{page}:{pageSize}:{GridColumnFilters.CacheText(_columnFilters)}:{sortBy}:{sortDescending}";

    /// <summary>What the Action column and its filter show for an action, its label or its name
    /// spelled out, never a raw resource key.</summary>
    private Func<string, string> ActionText => _actionText ??= action => AuditActionPresentation.Label(L, action);

    private void ApplyLogs(PaginatedResult<AuditLogDto> result)
    {
        _logs = result.Items;
        _count = result.TotalCount;
    }

    /// <summary>One page of the export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<AuditLogDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(20);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(AuditLogDto.Timestamp), fallbackDescending: true);
        return Api.Monitoring.GetAuditLogsAsync(page, pageSize, sortBy: sortBy, sortDescending: sortDescending,
            filters: args.ToApiFilters());
    }

    private async Task LoadData(GridLoadArgs args)
    {
        // The header filters drive the query, each one a real column filter of the endpoint.
        _columnFilters = args.ToApiFilters();
        var filters = _columnFilters;
        var (page, pageSize) = args.ToPageRequest(20);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(AuditLogDto.Timestamp), fallbackDescending: true);
        await Cache.RevalidateAsync(
            CacheKey(page, pageSize, sortBy, sortDescending),
            () => Api.Monitoring.GetAuditLogsAsync(page, pageSize, sortBy: sortBy, sortDescending: sortDescending, filters: filters),
            ApplyLogs,
            loading => _loading = loading,
            () => InvokeAsync(StateHasChanged));
    }

    /// <summary>A new entry refreshes the grid quietly, rows, page, scroll and filters kept. The
    /// grid is virtualized and keeps the rows it pulled, so the refresh goes through it.</summary>
    private Task RefreshData() => _grid?.Refresh() ?? LoadData(new GridLoadArgs());

    private async Task ShowDetailsDialogAsync(AuditLogDto log)
    {
        // Structured detail (actor / action / target / time) + server-verified chain-integrity badge.
        await Dialog.OpenAsync<AuditDetailDialog>(L["AuditEntryDetail"],
            new Dictionary<string, object?> { ["Log"] = log },
            new OmniDialogOptions { Width = "700px", CloseDialogOnOverlayClick = true, AutoFocusFirstElement = false });
    }

    private async Task VerifyChainAsync()
    {
        _verifyingChain = true;
        StateHasChanged();
        try
        {
            _chainResult = await Api.Monitoring.VerifyAuditChainAsync();
            if (_chainResult is not null)
            {
                Notify.Notify(
                    _chainResult.IsValid ? OmniSeverity.Success : OmniSeverity.Danger,
                    "AuditVerifyChain",
                    _chainResult.IsValid ? L["AuditChainIntact"] : L["AuditChainBroken"]);
            }
            else
            {
                Notify.Error("Error", "OperationFailed");
            }
        }
        catch (HttpRequestException)
        {
            _chainResult = null; // honest "unavailable", never an optimistic green
            Notify.Error("Error", "OperationFailed");
        }
        finally
        {
            _chainChecked = true;
            _verifyingChain = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private Task CopyDetailsAsync(string? details) => Clipboard.CopyAsync(details, L["Details"]);

    public async ValueTask DisposeAsync()
    {
        if (_liveEntries is not null)
            await _liveEntries.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
