// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Notifications;

public partial class NotificationsInbox : IDisposable
{
    private const int DefaultPageSize = 20;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private UserNotificationsFeed Feed { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    private List<NotificationDeliveryDto> _rows = [];
    private int _count;
    private bool _loading;
    private bool _markingAll;
    private UserNotificationPageRequest _filters = new();
    private int _page = 1;
    private int _pageSize = DefaultPageSize;
    private bool _sortDescending = true;
    private string _sortBy = nameof(NotificationDeliveryDto.CreatedAt);
    private int _lastUnreadCount = -1;
    private AetheusDataGrid<NotificationDeliveryDto>? _grid;

    // Recette R-224: the event types the Event column offers, and the texts of the closed filters.
    private UserNotificationFilterValuesDto _filterValues = new();
    private Func<string, string>? _statusText;
    private Func<string, string>? _yesNoText;
    private Func<string, string>? _eventText;
    private Func<string, string> StatusText => _statusText ??= GridFilterText.ForEnum<NotificationDeliveryStatus>(L);
    private Func<string, string> YesNoText => _yesNoText ??= GridFilterText.YesNo(L);
    private Func<string, string> EventText => _eventText ??= eventType => NotificationLabels.EventLabel(L, eventType);

    protected override async Task OnInitializedAsync()
    {
        // Recette R-189: a trail under the title, as on every page with an ancestor.
        Breadcrumb.Set(new BreadcrumbItem(L["Dashboard"], "/"), new BreadcrumbItem(L["MyNotifications"]));
        Feed.OnChanged += OnFeedChanged;
        await Feed.EnsureStartedAsync();
        await LoadFilterValuesAsync();
        await LoadData(new GridLoadArgs());
    }

    private async Task LoadFilterValuesAsync()
    {
        try
        {
            _filterValues = await Api.Notifications.GetFilterValuesAsync();
        }
        catch (HttpRequestException)
        {
            _filterValues = new UserNotificationFilterValuesDto();
        }
    }

    /// <summary>A new or newly read notification (from the bell, or pushed by the server) changes the unread
    /// count: the grid refreshes then, not on every poll that brings nothing new. Recette R-226: quietly,
    /// rows, page, scroll and filters kept, a new row in bold.</summary>
    private void OnFeedChanged() => _ = InvokeAsync(async () =>
    {
        var changed = _lastUnreadCount >= 0 && _lastUnreadCount != Feed.UnreadCount;
        _lastUnreadCount = Feed.UnreadCount;
        // Recette R-301: a mark-as-read of this page refreshes the grid itself, once, when it is done.
        // Refreshing here too ran a second load beside it; the reload emptied the grid's cache under the
        // refresh, which then took every row for a new one and set them all bold.
        if (changed && !_loading && !_acting)
        {
            await LoadFilterValuesAsync();
            await (_grid is not null ? _grid.Refresh() : FetchAsync());
        }
        else
            StateHasChanged();
    });

    /// <summary>Recette R2-124: one page of the export, with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<NotificationDeliveryDto>> ReadExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest(DefaultPageSize);
        var (sortBy, sortDescending) = args.ToSortRequest(nameof(NotificationDeliveryDto.CreatedAt), fallbackDescending: true);
        return Api.Notifications.GetMineAsync(new UserNotificationPageRequest
        {
            Filters = args.ToApiFilters(),
            Page = page,
            PageSize = pageSize,
            SortBy = sortBy,
            SortDescending = sortDescending
        });
    }

    private async Task LoadData(GridLoadArgs args)
    {
        (_page, _pageSize) = args.ToPageRequest(DefaultPageSize);
        (_sortBy, _sortDescending) = args.ToSortRequest(nameof(NotificationDeliveryDto.CreatedAt), fallbackDescending: true);
        // Recette R-224: each header filter travels as a column filter the endpoint applies itself (the
        // status and event lists, read yes/no, the subject and the date range).
        _filters = new UserNotificationPageRequest { Filters = args.ToApiFilters() };
        await FetchAsync();
    }

    /// <summary>The grid is virtualized: it keeps the rows it pulled, so replacing <c>_rows</c> alone does
    /// not show a changed row. A quiet refresh pulls again through <see cref="LoadData"/> and keeps the
    /// rows, scroll and filters (a reload would drop them and mark every row as new).</summary>
    private Task RefreshGridAsync() => _grid is not null ? _grid.Refresh() : FetchAsync();

    private bool _acting;

    /// <summary>Recette R-301: the action, then one refresh; the feed event it raises meanwhile only
    /// records the new unread count.</summary>
    private async Task ActThenRefreshAsync(Func<Task<bool>> action, string successMessage)
    {
        _acting = true;
        try
        {
            bool done;
            try
            {
                done = await action();
            }
            catch (HttpRequestException)
            {
                done = false;
            }
            if (done)
                Toast.Success("Saved", successMessage);
            else
                Toast.Error("Error", "OperationFailed");
            _lastUnreadCount = Feed.UnreadCount;
            await RefreshGridAsync();
        }
        finally
        {
            _acting = false;
        }
    }

    /// <summary>Recette R-314: opening an unread notification reads it. The dialog opens first; the mark,
    /// its count and its row follow while it is shown, quietly (no toast for a read that was not asked).</summary>
    private async Task OpenAsync(NotificationDeliveryDto row)
    {
        var dialog = Dialog.OpenAsync<InboxNotificationDialog>(L["NotificationDetails"].Value,
            new Dictionary<string, object?> { [nameof(InboxNotificationDialog.Notification)] = row });
        if (row.ReadAt is null)
            await MarkOpenedReadAsync(row);
        await dialog;
    }

    private async Task MarkOpenedReadAsync(NotificationDeliveryDto row)
    {
        _acting = true;
        try
        {
            try
            {
                if (!await Feed.MarkReadAsync(row.Id))
                    return;
            }
            catch (HttpRequestException)
            {
                // The global error handler reports it; the row stays unread and its button stays.
                return;
            }
            _lastUnreadCount = Feed.UnreadCount;
            await RefreshGridAsync();
        }
        finally
        {
            _acting = false;
        }
    }

    private async Task FetchAsync()
    {
        _loading = true;
        try
        {
            var result = await Api.Notifications.GetMineAsync(_filters with
            {
                Page = _page,
                PageSize = _pageSize,
                SortBy = _sortBy,
                SortDescending = _sortDescending
            });
            _rows = result.Items;
            _count = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            // The global error handler reports the failure; the grid keeps what it showed.
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private Task MarkReadAsync(NotificationDeliveryDto row) =>
        ActThenRefreshAsync(() => Feed.MarkReadAsync(row.Id), "NotificationMarkedRead");

    private async Task MarkAllReadAsync()
    {
        _markingAll = true;
        try
        {
            await ActThenRefreshAsync(Feed.MarkAllReadAsync, "NotificationsAllMarkedRead");
        }
        finally
        {
            _markingAll = false;
        }
    }

    public void Dispose() => Feed.OnChanged -= OnFeedChanged;
}
