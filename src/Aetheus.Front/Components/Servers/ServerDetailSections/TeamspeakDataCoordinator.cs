// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>TeamSpeak state and paged grids refresh on server heartbeats and task completion. Grid
/// reloads preserve their page, sort and header filters.</summary>
internal sealed class TeamspeakDataCoordinator(ApiClient api) : IAsyncDisposable
{
    private readonly ApiClient _api = api;
    private CancellationTokenSource? _lifetimeCts;
    private Func<Task>? _changed;
    private long _generation;
    private int _serverId;

    public TeamspeakDataDto State { get; private set; } = new();
    public List<TeamspeakClientDto> Clients { get; private set; } = [];
    public List<TeamspeakChannelDto> Channels { get; private set; } = [];
    public List<TeamspeakBanDto> Bans { get; private set; } = [];
    public int ClientsCount { get; private set; }
    public int ChannelsCount { get; private set; }
    public int BansCount { get; private set; }
    public bool ClientsLoading { get; private set; }
    public bool ChannelsLoading { get; private set; }
    public bool BansLoading { get; private set; }

    public async Task ResetAsync(int serverId, TeamspeakDataDto initialState, Func<Task> changed)
    {
        await CancelLifetimeAsync().ConfigureAwait(false);
        _serverId = serverId;
        _changed = changed;
        _generation++;
        State = initialState with { Clients = [], Channels = [], Bans = [] };
        Clients = [];
        Channels = [];
        Bans = [];
        ClientsCount = initialState.OnlineClients;
        ChannelsCount = initialState.ChannelCount;
        BansCount = 0;
        ClientsLoading = false;
        ChannelsLoading = false;
        BansLoading = false;

        _lifetimeCts = new CancellationTokenSource();
        await RefreshStateAsync(_generation, _lifetimeCts.Token).ConfigureAwait(false);
    }

    public Task LoadClientsAsync(
        int page, int pageSize, string? search, string? sortBy, bool sortDescending,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null) =>
        LoadClientsCoreAsync(new PageQuery(page, pageSize, search, sortBy, sortDescending, filters), _generation, CurrentToken);

    public Task LoadChannelsAsync(
        int page, int pageSize, string? search, string? sortBy, bool sortDescending,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null) =>
        LoadChannelsCoreAsync(new PageQuery(page, pageSize, search, sortBy, sortDescending, filters), _generation, CurrentToken);

    public Task LoadBansAsync(
        int page, int pageSize, string? search, string? sortBy, bool sortDescending,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? filters = null) =>
        LoadBansCoreAsync(new PageQuery(page, pageSize, search, sortBy, sortDescending, filters), _generation, CurrentToken);

    /// <summary>Grid loads use the requested block, sort and header filters.</summary>
    public Task LoadClientsAsync(GridLoadArgs args) =>
        LoadClientsCoreAsync(ToQuery(args, "Nickname"), _generation, CurrentToken);

    public Task LoadChannelsAsync(GridLoadArgs args) =>
        LoadChannelsCoreAsync(ToQuery(args, "Order"), _generation, CurrentToken);

    public Task LoadBansAsync(GridLoadArgs args) =>
        LoadBansCoreAsync(ToQuery(args, "Created", defaultDescending: true), _generation, CurrentToken);

    // One page of each list's export, the same query as the grid's load, without touching the rows
    // the section shows. A page that cannot be read throws and fails the export.
    public Task<PaginatedResult<TeamspeakClientDto>> ReadClientsExportAsync(GridLoadArgs args)
    {
        var query = ToQuery(args, "Nickname");
        return _api.Teamspeak.GetTeamspeakClientsAsync(_serverId, query.Page, query.PageSize, query.Search,
            query.SortBy, query.SortDescending, CurrentToken, query.Filters);
    }

    public Task<PaginatedResult<TeamspeakChannelDto>> ReadChannelsExportAsync(GridLoadArgs args)
    {
        var query = ToQuery(args, "Order");
        return _api.Teamspeak.GetTeamspeakChannelsAsync(_serverId, query.Page, query.PageSize, query.Search,
            query.SortBy, query.SortDescending, CurrentToken, query.Filters);
    }

    public Task<PaginatedResult<TeamspeakBanDto>> ReadBansExportAsync(GridLoadArgs args)
    {
        var query = ToQuery(args, "Created", defaultDescending: true);
        return _api.Teamspeak.GetTeamspeakBansAsync(_serverId, query.Page, query.PageSize, query.Search,
            query.SortBy, query.SortDescending, CurrentToken, query.Filters);
    }

    private static PageQuery ToQuery(GridLoadArgs args, string defaultSort, bool defaultDescending = false)
    {
        var pageSize = args.Top ?? 25;
        var page = ((args.Skip ?? 0) / pageSize) + 1;
        var filters = args.ToApiFilters();
        if (string.IsNullOrWhiteSpace(args.OrderBy))
            return new PageQuery(page, pageSize, null, defaultSort, defaultDescending, filters);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new PageQuery(page, pageSize, null, parts[0],
            parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase), filters);
    }

    /// <summary>Fetches and publishes the server's TeamSpeak state (the header badges and the stats tab).</summary>
    public Task RefreshStateAsync() => RefreshStateAsync(_generation, CurrentToken);

    public async Task<List<TeamspeakChannelDto>> LoadAllChannelsAsync()
    {
        const int pageSize = 200;
        var result = new List<TeamspeakChannelDto>();
        for (var page = 1; ; page++)
        {
            var response = await _api.Teamspeak.GetTeamspeakChannelsAsync(
                _serverId, page, pageSize, sortBy: "Name", ct: CurrentToken).ConfigureAwait(false);
            result.AddRange(response.Items);
            if (page * pageSize >= response.TotalCount || response.Items.Count == 0)
                return result;
        }
    }

    private async Task RefreshStateAsync(long generation, CancellationToken ct)
    {
        var state = await _api.Teamspeak.GetTeamspeakStateAsync(_serverId, ct).ConfigureAwait(false);
        if (!IsCurrent(generation, ct)) return;
        State = state;
        await NotifyChangedAsync().ConfigureAwait(false);
    }

    private async Task LoadClientsCoreAsync(PageQuery query, long generation, CancellationToken ct)
    {
        ClientsLoading = true;
        await NotifyChangedAsync().ConfigureAwait(false);
        try
        {
            var result = await _api.Teamspeak.GetTeamspeakClientsAsync(
                _serverId, query.Page, query.PageSize, query.Search,
                query.SortBy, query.SortDescending, ct, query.Filters).ConfigureAwait(false);
            if (!IsCurrent(generation, ct)) return;
            Clients = result.Items;
            ClientsCount = result.TotalCount;
        }
        finally
        {
            if (IsCurrent(generation, ct))
            {
                ClientsLoading = false;
                await NotifyChangedAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task LoadChannelsCoreAsync(PageQuery query, long generation, CancellationToken ct)
    {
        ChannelsLoading = true;
        await NotifyChangedAsync().ConfigureAwait(false);
        try
        {
            var result = await _api.Teamspeak.GetTeamspeakChannelsAsync(
                _serverId, query.Page, query.PageSize, query.Search,
                query.SortBy, query.SortDescending, ct, query.Filters).ConfigureAwait(false);
            if (!IsCurrent(generation, ct)) return;
            Channels = result.Items;
            ChannelsCount = result.TotalCount;
        }
        finally
        {
            if (IsCurrent(generation, ct))
            {
                ChannelsLoading = false;
                await NotifyChangedAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task LoadBansCoreAsync(PageQuery query, long generation, CancellationToken ct)
    {
        BansLoading = true;
        await NotifyChangedAsync().ConfigureAwait(false);
        try
        {
            var result = await _api.Teamspeak.GetTeamspeakBansAsync(
                _serverId, query.Page, query.PageSize, query.Search,
                query.SortBy, query.SortDescending, ct, query.Filters).ConfigureAwait(false);
            if (!IsCurrent(generation, ct)) return;
            Bans = result.Items;
            BansCount = result.TotalCount;
        }
        finally
        {
            if (IsCurrent(generation, ct))
            {
                BansLoading = false;
                await NotifyChangedAsync().ConfigureAwait(false);
            }
        }
    }

    private CancellationToken CurrentToken => _lifetimeCts?.Token ?? CancellationToken.None;
    private bool IsCurrent(long generation, CancellationToken ct) =>
        generation == _generation && !ct.IsCancellationRequested;
    private Task NotifyChangedAsync() => _changed?.Invoke() ?? Task.CompletedTask;

    private async Task CancelLifetimeAsync()
    {
        if (_lifetimeCts is null) return;
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();
        _lifetimeCts = null;
    }

    public ValueTask DisposeAsync() => new(CancelLifetimeAsync());

    private sealed record PageQuery(
        int Page, int PageSize, string? Search, string? SortBy, bool SortDescending,
        IReadOnlyList<Aetheus.Shared.Components.Shared.GridFilter>? Filters);
}
