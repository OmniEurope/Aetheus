// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public abstract class RealtimeAdminGridPageBase<T> : ComponentBase, IAsyncDisposable
    where T : notnull
{
    [Inject] protected ApiClient Api { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected AuthStateProvider Auth { get; set; } = default!;
    [Inject] protected IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] protected OmniDialogService Dialog { get; set; } = default!;
    [Inject] protected BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] protected HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] protected UiActions Ui { get; set; } = default!;

    protected AetheusDataGrid<T>? _grid;
    protected int _totalCount;
    protected string _search = string.Empty;
    protected bool _loading;

    private AdminEntitySubscription? _adminSubscription;
    private readonly TrailingReloadCoalescer _realtimeReload = new(500);

    protected async Task<bool> InitializeAdminAsync(string entity, string titleKey)
    {
        if (!AdminPageEntry.TryEnter(Auth, Nav, Breadcrumb, L, titleKey))
            return false;

        _adminSubscription = new AdminEntitySubscription(HubFactory);
        await _adminSubscription.StartAsync(
            entity,
            () => InvokeAsync(() => _realtimeReload.RequestAsync(RefreshAsync))).ConfigureAwait(false);
        return true;
    }

    protected Task ReloadAsync() => _grid?.Reload() ?? Task.CompletedTask;

    /// <summary>A change pushed by another session refreshes quietly (rows, page, scroll and
    /// filters kept); the page's own actions keep <see cref="ReloadAsync"/>.</summary>
    protected Task RefreshAsync() => _grid?.Refresh() ?? Task.CompletedTask;

    // Remote-virtualized grids stay on "page 1", so a plain GoToPage(0) would not reload them.
    protected Task ResetSearchAsync() => _grid?.GoToPage(0, forceReload: true) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_adminSubscription is not null)
            await _adminSubscription.DisposeAsync().ConfigureAwait(false);
    }
}
