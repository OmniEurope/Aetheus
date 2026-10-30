// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.AppBackups;

public partial class Backups : IAsyncDisposable
{
    [Parameter] public int? ProjectId { get; set; }

    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;

    private List<BackupPolicyDto> _policies = [];
    private AetheusDataGrid<BackupPolicyDto>? _policyGrid;
    private int _policiesTotalCount;
    private string? _policySearch;
    private readonly List<DropItem> _projects = [];
    private readonly List<DropItem> _servers = [];
    private bool _loading = true;
    private bool _saving;

    private PolicyForm? _form;                     // non-null while the create/edit panel is open
    private BackupPolicyDto? _runsFor;             // policy whose run history is shown
    private List<BackupRunDto> _runs = [];
    private AetheusDataGrid<BackupRunDto>? _runsGrid;
    private int _runsTotalCount;
    private string? _runsSearch;
    private bool _runsLoading;
    private HubConnection? _hubConnection;
    private readonly TrailingReloadCoalescer _realtimeReload = new(500);

    // The header filters of both grids, sent with every page request, and their texts.
    private List<GridFilter> _policyFilters = [];
    private List<GridFilter> _runFilters = [];
    private IReadOnlyList<string> _projectNames = [];
    private IReadOnlyList<string> _serverNames = [];
    private Func<string, string>? _engineText;
    private Func<string, string>? _enabledText;
    private Func<string, string>? _runStatusText;
    private Func<string, string>? _restoreText;
    private Func<string, string> EngineText => _engineText ??= GridFilterText.ForEnum<BackupDbEngine>(L);
    private Func<string, string> EnabledText => _enabledText ??= value =>
        bool.TryParse(value, out var enabled) ? L[enabled ? "BackupEnabledYes" : "BackupEnabledNo"].Value : value;
    private Func<string, string> RunStatusText => _runStatusText ??= GridFilterText.ForEnum<BackupRunStatus>(L);
    private Func<string, string> RestoreText => _restoreText ??= value =>
        Enum.TryParse<RestoreCheckStatus>(value, out var status) ? RestoreLabel(status) : value;

    private static readonly BackupDbEngine[] Engines = [BackupDbEngine.None, BackupDbEngine.Postgres, BackupDbEngine.MySql];
    private List<EnumOption<BackupDbEngine>> _engineOptions = [];

    private readonly record struct DropItem(int Id, string Name);
    private readonly record struct EnumOption<T>(string Text, T Value);

    private sealed class PolicyForm
    {
        public int Id { get; set; } // 0 = new

        [Required]
        [StringLength(120, MinimumLength = 1)]
        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        [Range(1, int.MaxValue, ErrorMessage = "SelectProject")]
        public int ProjectId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "SelectServer")]
        public int ServerId { get; set; }

        public BackupDbEngine DbEngine { get; set; } = BackupDbEngine.Postgres;
        [StringLength(255)] public string? DbHost { get; set; }
        [Range(1, 65535)] public int? DbPort { get; set; }
        [StringLength(120)] public string? DbName { get; set; }
        [StringLength(120)] public string? DbUser { get; set; }
        [StringLength(256)] public string? DbPassword { get; set; }
        public string FilePathsText { get; set; } = string.Empty;

        [Required]
        [StringLength(120)]
        public string ScheduleCron { get; set; } = "0 3 * * *";

        [Range(1, 365)]
        public int RetentionCount { get; set; } = 7;

        [StringLength(120)]
        public string? RestoreCheckCron { get; set; }
    }

    protected override async Task OnInitializedAsync()
    {
        _engineOptions = Engines.Select(value => new EnumOption<BackupDbEngine>(L.Localize(value), value)).ToList();
        if (!ProjectId.HasValue)
            Breadcrumb.Set(new BreadcrumbItem(L["Backups"]));
        try
        {
            foreach (var p in await Api.Projects.GetAllProjectsAsync())
                _projects.Add(new DropItem(p.Id, p.Name));
            foreach (var s in await Api.Servers.GetAllServersAsync())
                _servers.Add(new DropItem(s.Id, s.Name));
            _projectNames = DistinctNames(_projects);
            _serverNames = DistinctNames(_servers);
        }
        catch (HttpRequestException)
        {
            // Expired-JWT 401 during load must not trip the ErrorBoundary; grids just show empty.
        }
        finally
        {
            _loading = false;
        }
        await StartRealtimeAsync();
    }

    private async Task StartRealtimeAsync()
    {
        try
        {
            _hubConnection = HubFactory.Create("entities");
            _hubConnection.On<int>(OperationalRealtimeEvents.BackupChanged, projectId =>
                !ProjectId.HasValue || ProjectId.Value == projectId
                    ? InvokeAsync(() => _realtimeReload.RequestAsync(ReloadRealtimeAsync))
                    : Task.CompletedTask);
            _hubConnection.RejoinOnReconnect(() => InvokeAsync(async () =>
            {
                await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
                await ReloadRealtimeAsync();
            }));
            await _hubConnection.StartAsync();
            await _hubConnection.InvokeAsync("JoinEntityUpdates", ResourceType.Project);
        }
        catch { /* SignalR is best-effort; manual refresh remains available. */ }
    }

    private static List<string> DistinctNames(IEnumerable<DropItem> items) => [.. items
        .Select(item => item.Name)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)];

    // A live backup event refreshes both grids in place, page, sort and filters kept.
    private async Task ReloadRealtimeAsync()
    {
        var policyReload = _policyGrid?.Refresh() ?? Task.CompletedTask;
        var runsReload = _runsFor is null
            ? Task.CompletedTask
            : _runsGrid?.Refresh() ?? Task.CompletedTask;
        await Task.WhenAll(policyReload, runsReload);
        StateHasChanged();
    }

    /// <summary>One page of the policies export, with the search, sort and column filters of the
    /// grid.</summary>
    internal Task<PaginatedResult<BackupPolicyDto>> ReadPoliciesExportAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args, "Name");
        return Api.Security.GetBackupPoliciesAsync(
            page, pageSize, _policySearch, sortBy, sortDescending, ProjectId, filters: args.ToApiFilters());
    }

    internal async Task OnLoadPoliciesAsync(GridLoadArgs args)
    {
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args, "Name");
        _policyFilters = args.ToApiFilters();
        _loading = true;
        try
        {
            var result = await Api.Security.GetBackupPoliciesAsync(
                page, pageSize, _policySearch, sortBy, sortDescending, ProjectId, filters: _policyFilters);
            _policies = result.Items;
            _policiesTotalCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            _policies = [];
            _policiesTotalCount = 0;
        }
        finally { _loading = false; }
    }

    private async Task ResetPoliciesAsync()
    {
        if (_policyGrid is not null) await _policyGrid.GoToPage(0, forceReload: true);
    }

    private async Task ReloadAsync()
    {
        if (_policyGrid is not null) await _policyGrid.Reload();
    }

    private void NewPolicy() => _form = new PolicyForm { ProjectId = ProjectId ?? 0 };

    private void EditPolicy(BackupPolicyDto p) => _form = new PolicyForm
    {
        Id = p.Id,
        Name = p.Name,
        Enabled = p.Enabled,
        ProjectId = p.ProjectId,
        ServerId = p.ServerId,
        DbEngine = p.DbEngine,
        DbHost = p.DbHost,
        DbPort = p.DbPort,
        DbName = p.DbName,
        DbUser = p.DbUser,
        DbPassword = null, // null = keep existing password
        FilePathsText = string.Join('\n', p.FilePaths),
        ScheduleCron = p.ScheduleCron,
        RetentionCount = p.RetentionCount,
        RestoreCheckCron = p.RestoreCheckCron
    };

    private void CancelForm() => _form = null;

    private async Task SaveAsync()
    {
        if (_form is null) return;
        _saving = true;
        try
        {
            var files = _form.FilePathsText
                .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (_form.Id == 0)
            {
                var created = await Api.Security.CreateBackupPolicyAsync(new CreateBackupPolicyRequest
                {
                    Name = _form.Name,
                    ProjectId = _form.ProjectId,
                    ServerId = _form.ServerId,
                    DbEngine = _form.DbEngine,
                    DbHost = _form.DbHost,
                    DbPort = _form.DbPort,
                    DbName = _form.DbName,
                    DbUser = _form.DbUser,
                    DbPassword = _form.DbPassword,
                    FilePaths = files,
                    ScheduleCron = _form.ScheduleCron,
                    RetentionCount = _form.RetentionCount,
                    RestoreCheckCron = _form.RestoreCheckCron
                });
                if (created is null) { Toast.Error("BackupSaveFailed", "BackupSaveFailed"); return; }
            }
            else
            {
                var updated = await Api.Security.UpdateBackupPolicyAsync(_form.Id, new UpdateBackupPolicyRequest
                {
                    Name = _form.Name,
                    Enabled = _form.Enabled,
                    DbEngine = _form.DbEngine,
                    DbHost = _form.DbHost,
                    DbPort = _form.DbPort,
                    DbName = _form.DbName,
                    DbUser = _form.DbUser,
                    DbPassword = _form.DbPassword,
                    FilePaths = files,
                    ScheduleCron = _form.ScheduleCron,
                    RetentionCount = _form.RetentionCount,
                    RestoreCheckCron = _form.RestoreCheckCron
                });
                if (updated is null) { Toast.Error("BackupSaveFailed", "BackupSaveFailed"); return; }
            }

            Toast.Success("BackupSaved", "BackupSaved");
            _form = null;
            await ReloadAsync();
        }
        catch (HttpRequestException)
        {
            Toast.Error("BackupSaveFailed", "BackupSaveFailed");
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task DeletePolicyAsync(BackupPolicyDto p)
    {
        var confirmed = await Dialog.Confirm(
            string.Format(L["BackupDeleteConfirm"].Value, p.Name), L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var status = await Api.Security.DeleteBackupPolicyAsync(p.Id);
            if (status.Success)
            {
                Toast.Success("BackupDeleted", "BackupDeleted");
                if (_runsFor?.Id == p.Id) _runsFor = null;
                await ReloadAsync();
            }
            else
            {
                Toast.Error("BackupDeleteFailed", "BackupDeleteFailed");
            }
        });
    }

    private async Task RunNowAsync(BackupPolicyDto p)
    {
        var status = await Api.Security.RunBackupNowAsync(p.Id);
        if (status.Success) Toast.Success("BackupRunQueued", "BackupRunQueued");
        else Toast.Error("BackupRunFailed", "BackupRunFailed");
    }

    private async Task ShowRunsAsync(BackupPolicyDto p)
    {
        var runsGrid = _runsFor is not null ? _runsGrid : null;
        _runsFor = p;
        _runs = [];
        _runsTotalCount = 0;
        _runsSearch = null;
        await InvokeAsync(StateHasChanged);
        // The runs grid stays mounted when another policy is picked; it reloads for that policy
        // instead of keeping the rows (or the scrolled blocks) of the previous one. A grid
        // appearing for the first time loads itself.
        if (runsGrid is not null) await runsGrid.Reload();
    }

    /// <summary>One page of the runs export of the policy shown, with the grid's search, sort and
    /// column filters.</summary>
    internal Task<PaginatedResult<BackupRunDto>> ReadRunsExportAsync(GridLoadArgs args)
    {
        if (_runsFor is null) return Task.FromResult(new PaginatedResult<BackupRunDto>());
        var (page, pageSize) = args.ToPageRequest(10);
        var (sortBy, sortDescending) = GetSort(args, "StartedAt", true);
        return Api.Security.GetBackupRunsAsync(
            _runsFor.Id, page, pageSize, _runsSearch, sortBy, sortDescending, filters: args.ToApiFilters());
    }

    internal async Task OnLoadRunsAsync(GridLoadArgs args)
    {
        if (_runsFor is null) return;
        var policyId = _runsFor.Id;
        var (page, pageSize) = args.ToPageRequest(10);
        var (sortBy, sortDescending) = GetSort(args, "StartedAt", true);
        _runFilters = args.ToApiFilters();
        _runsLoading = true;
        try
        {
            var result = await Api.Security.GetBackupRunsAsync(
                policyId, page, pageSize, _runsSearch, sortBy, sortDescending, filters: _runFilters);
            if (_runsFor?.Id != policyId) return;
            _runs = result.Items;
            _runsTotalCount = result.TotalCount;
        }
        catch (HttpRequestException)
        {
            if (_runsFor?.Id != policyId) return;
            _runs = [];
            _runsTotalCount = 0;
        }
        finally { _runsLoading = false; }
    }

    private async Task ResetRunsAsync()
    {
        if (_runsGrid is not null) await _runsGrid.GoToPage(0, forceReload: true);
    }

    private static (string SortBy, bool Descending) GetSort(
        GridLoadArgs args, string defaultSort, bool defaultDescending = false)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return (defaultSort, defaultDescending);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1
            ? string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase)
            : defaultDescending);
    }

    private string ProjectName(int id) => _projects.FirstOrDefault(x => x.Id == id).Name ?? id.ToString();
    private string ServerName(int id) => _servers.FirstOrDefault(x => x.Id == id).Name ?? id.ToString();

    private OmniTone RunStatusStyle(BackupRunStatus s) => s switch
    {
        BackupRunStatus.Succeeded => OmniTone.Success,
        BackupRunStatus.Failed => OmniTone.Danger,
        _ => OmniTone.Accent
    };

    private OmniTone RestoreStyle(RestoreCheckStatus s) => s switch
    {
        RestoreCheckStatus.Verified => OmniTone.Success,
        RestoreCheckStatus.Failed => OmniTone.Danger,
        _ => OmniTone.Neutral
    };

    private string RestoreLabel(RestoreCheckStatus s) => s switch
    {
        RestoreCheckStatus.Verified => L["BackupRestoreVerified"],
        RestoreCheckStatus.Failed => L["BackupRestoreFailed"],
        _ => L["BackupRestoreUnverified"]
    };

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is null) return;
        await _hubConnection.LeaveEntityUpdatesAndDisposeAsync(ResourceType.Project);
        _hubConnection = null;
    }

    /// <summary>The export writes the names the cells show.</summary>
    private Func<BackupPolicyDto, object?> ProjectExport => _projectExport ??= policy => ProjectName(policy.ProjectId);
    private Func<BackupPolicyDto, object?> ServerExport => _serverExport ??= policy => ServerName(policy.ServerId);
    private Func<BackupPolicyDto, object?>? _projectExport;
    private Func<BackupPolicyDto, object?>? _serverExport;
}
