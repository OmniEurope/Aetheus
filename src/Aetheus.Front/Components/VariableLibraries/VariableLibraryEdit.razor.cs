// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
namespace Aetheus.Front.Components.VariableLibraries;

public partial class VariableLibraryEdit : IAsyncDisposable
{
    [Inject] private BusyVeil Veil { get; set; } = default!;
    [Inject] private HubConnectionFactory HubFactory { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private ListCacheService Cache { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;

    [Parameter] public int? Id { get; set; }

    [SupplyParameterFromQuery] public int? ProjectId { get; set; }
    [SupplyParameterFromQuery] public int? EnvironmentId { get; set; }
    [SupplyParameterFromQuery] public int? ProjectServerId { get; set; }

    /// <summary>The name a new library starts with, when a page links here for one in particular.</summary>
    [SupplyParameterFromQuery] public string? Name { get; set; }

    /// <summary>The entry a link points at (R-10): the Key column of the grid starts filtered on it, a
    /// filter the reader sees and can remove.</summary>
    [SupplyParameterFromQuery(Name = "key")] public string? FilterKey { get; set; }

    /// <summary>The page that sent the user here to check what it created (the pipeline setup
    /// wizard's summary); Back returns to it when it is local.</summary>
    [SupplyParameterFromQuery(Name = LocalReturnUrl.Parameter)] public string? ReturnUrl { get; set; }

    private VariableLibraryDetailDto? _detail;
    private LibraryModel _model = new();
    private List<ProjectDto> _projects = [];
    private bool _isNew => Id is null or 0;
    private bool _saving;
    private bool _loading = true;
    private bool _loadFailed;
    private (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId)? _previousKey;
    private int _loadGeneration;

    private NewEntryModel _newEntry = new();
    private string _editEntryKey = string.Empty;
    private string _editEntryValue = string.Empty;
    private VariableEntryDto? _editingEntry;
    private AetheusDataGrid<VariableEntryDto>? _entriesGrid;
    private List<Aetheus.Shared.Components.Shared.GridFilter> _entryFilters = [];
    private List<VariableEntryDto> _entries = [];
    private int _entryCount;
    private bool _entriesLoading;
    private bool _entriesLoadFailed;
    private int _entryPage = 1;
    private int _entryPageSize = 25;
    private string _entrySortBy = "Key";
    private bool _entrySortDescending;

    protected override async Task OnParametersSetAsync()
    {
        var key = (Id, ProjectId, EnvironmentId, ProjectServerId);
        if (key == _previousKey) return;
        _previousKey = key;
        var generation = ++_loadGeneration;
        var isNew = key.Id is null or 0;
        ResetLoadState();
        await LoadLibraryAsync(key, generation, isNew);
        if (!isNew && _detail is not null) await LoadUsageAsync(key.Id!.Value, generation);

        // Publish the parent project so the NavMenu keeps the project's submenu open
        // while we're editing one of its variable libraries.
        ProjectNav.Set(_model.ProjectId);
        ReassertBreadcrumb(isNew);
    }

    private IReadOnlyList<ResourceUsageUserDto>? _usage;
    private bool _usageLoaded;
    private int _usageRequest;
    private PipelineChangeSubscription? _pipelineChanges;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusNewEntryKey)
        {
            _focusNewEntryKey = false;
            await JS.InvokeVoidAsync("Aetheus.focusById", NewEntryKeyId);
        }
        if (_focusNewEntryValue)
        {
            _focusNewEntryValue = false;
            await JS.InvokeVoidAsync("Aetheus.focusById", NewEntryValueId);
        }
        if (!firstRender) return;
        // R-64: a pipeline created, changed or deleted while the page is open changes who reads the library.
        _pipelineChanges = new PipelineChangeSubscription(HubFactory, () => InvokeAsync(async () =>
        {
            await RefreshUsageAsync();
            StateHasChanged();
        }));
        await _pipelineChanges.StartAsync();
    }

    /// <summary>Who reads this library (R-65), asked after the page is shown and again when a pipeline
    /// changes or the library is saved (R-64). A failed call keeps what the line showed (nothing, on the
    /// first load) rather than saying "Unused"; only the latest answer is applied.</summary>
    private async Task LoadUsageAsync(int libraryId, int generation)
    {
        var request = ++_usageRequest;
        try
        {
            var usage = await Api.Variables.GetResourceUsageAsync();
            if (generation != _loadGeneration || request != _usageRequest) return;
            _usage = usage.Libraries.GetValueOrDefault(libraryId);
            _usageLoaded = true;
        }
        catch (HttpRequestException) { } // the page stays usable without the usage line
    }

    private Task RefreshUsageAsync() =>
        _isNew || _detail is null ? Task.CompletedTask : LoadUsageAsync(Id!.Value, _loadGeneration);

    public async ValueTask DisposeAsync()
    {
        if (_pipelineChanges is not null)
            await _pipelineChanges.DisposeAsync();
    }

    private void ReassertBreadcrumb(bool isNew)
    {
        var current = new BreadcrumbItem(isNew ? L["NewVariableLibrary"] : _detail?.Name ?? L["VariableLibrary"]);
        Breadcrumb.SetProjectResource(
            _model.ProjectId, _detail?.ProjectName, _projects,
            L["Projects"], L["Project"], L["VariableLibraries"],
            "libraries", "/variable-libraries", current);
    }

    private void ResetLoadState()
    {
        _detail = null;
        _model = new LibraryModel();
        ResetEntryBuffers();
        _entries = [];
        _entryCount = 0;
        _entriesLoadFailed = false;
        _entryPage = 1;
        _entryPageSize = 25;
        _entrySortBy = "Key";
        _entrySortDescending = false;
        _loading = true;
        _loadFailed = false;
        _usage = null;
        _usageLoaded = false;
    }

    private async Task LoadLibraryAsync(
        (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId) key,
        int generation,
        bool isNew)
    {
        try
        {
            var projects = await Api.Projects.GetAllProjectsAsync();
            var detail = isNew
                ? null
                : await Api.Variables.GetVariableLibraryDetailAsync(key.Id!.Value);
            if (generation != _loadGeneration || key != (Id, ProjectId, EnvironmentId, ProjectServerId)) return;
            ApplyLoadedLibrary(key, projects, detail, isNew);
            // The grid never fires LoadData while Data is pre-bound to a non-null list, so the entries
            // grid would stay empty forever next to a header count read from the detail endpoint.
            if (detail is not null)
                await LoadEntryPageAsync();
        }
        catch (HttpRequestException)
        {
            if (generation == _loadGeneration)
                _loadFailed = true;
        }
        finally
        {
            if (generation == _loadGeneration)
                _loading = false;
        }
    }

    private void ApplyLoadedLibrary(
        (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId) key,
        List<ProjectDto> projects,
        VariableLibraryDetailDto? detail,
        bool isNew)
    {
        _projects = projects;
        _detail = detail;
        if (detail is not null)
        {
            _entryCount = detail.EntryCount;
            _model = new LibraryModel
            {
                Name = detail.Name,
                Description = detail.Description,
                ProjectId = detail.ProjectId,
                EnvironmentId = detail.EnvironmentId,
                ProjectServerId = detail.ProjectServerId
            };
        }
        else if (isNew)
        {
            SelectInitialScope(key, projects);
            // The readiness findings link here with the name of the missing library (R2-22).
            if (!string.IsNullOrWhiteSpace(Name)) _model.Name = Name;
        }
    }

    private void SelectInitialScope(
        (int? Id, int? ProjectId, int? EnvironmentId, int? ProjectServerId) key,
        IReadOnlyCollection<ProjectDto> projects)
    {
        if (key.ProjectId is > 0 && projects.Any(p => p.Id == key.ProjectId.Value))
            _model.ProjectId = key.ProjectId;
        else if (key.EnvironmentId is > 0)
            _model.EnvironmentId = key.EnvironmentId;
        else if (key.ProjectServerId is > 0)
            _model.ProjectServerId = key.ProjectServerId;
    }

    private async Task OnSubmit()
    {
        _saving = true;
        try
        {
            if (_isNew)
            {
                var created = await Api.Variables.CreateVariableLibraryAsync(new CreateVariableLibraryRequest
                {
                    Name = _model.Name,
                    Description = _model.Description,
                    ProjectId = _model.ProjectId,
                    EnvironmentId = _model.EnvironmentId,
                    ProjectServerId = _model.ProjectServerId
                });
                if (created is not null)
                {
                    Toast.Success("Created", "VariableLibraryCreated");
                    // R-32: created from a checklist or another page, the user goes back to it.
                    // R-24: created for a missing key, the new library opens on that key to ask for its value.
                    Nav.NavigateTo(!string.IsNullOrWhiteSpace(FilterKey)
                        ? LocalReturnUrl.Append($"/variable-libraries/{created.Id}?key={Uri.EscapeDataString(FilterKey.Trim())}", ReturnUrl)
                        : LocalReturnUrl.Validated(ReturnUrl) ?? $"/variable-libraries/{created.Id}");
                }
            }
            else
            {
                var updated = await Api.Variables.UpdateVariableLibraryAsync(Id!.Value, new UpdateVariableLibraryRequest
                {
                    Name = _model.Name,
                    Description = _model.Description,
                    ProjectId = _model.ProjectId,
                    EnvironmentId = _model.EnvironmentId,
                    ProjectServerId = _model.ProjectServerId,
                    RowVersion = _detail?.RowVersion ?? Guid.Empty
                });
                if (updated is not null)
                {
                    Toast.Success("Saved", "VariableLibrarySaved");
                    _editingProperties = false;
                    await ReloadDetail();
                }
            }
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>The library's properties form, shown on demand from the ⋮ menu.</summary>
    private bool _editingProperties;

    private void EditProperties() => _editingProperties = true;

    private void CancelPropertiesEdit()
    {
        _editingProperties = false;
        if (_detail is not null)
        {
            _model = new LibraryModel
            {
                Name = _detail.Name,
                Description = _detail.Description,
                ProjectId = _detail.ProjectId,
                EnvironmentId = _detail.EnvironmentId,
                ProjectServerId = _detail.ProjectServerId
            };
        }
    }

    private Task CopyAsync(string text) => Clipboard.CopyAsync(text);

    private async Task RetryLoadAsync()
    {
        _previousKey = null;
        await OnParametersSetAsync();
    }

    private async Task OnDelete()
    {
        var confirmed = await Dialog.Confirm(L["DeleteVariableLibraryConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        await Veil.RunAsync(async () =>
        {
            var status = await Api.Variables.DeleteVariableLibraryAsync(Id!.Value);
            if (!status.Success)
            {
                Toast.Error("Error", "OperationFailed");
                return;
            }
            Cache.InvalidatePrefix("variable-libraries:"); // S-TECH-SWIV
            Toast.Success("Deleted", "Deleted");
            // R-70: back to the list the user came from (a project's libraries, a server's), else the global list.
            Nav.NavigateTo(LocalReturnUrl.Validated(ReturnUrl) ?? "/variable-libraries");
        });
    }

    private async Task AddEntry(NewEntryModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Key))
            return;

        var outcome = await Api.Variables.CreateVariableEntryAsync(Id!.Value, new CreateVariableEntryRequest
        {
            Key = model.Key,
            Value = model.Value
        });
        if (outcome.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // The key already exists, so nothing was added: say why.
            Toast.Error("Error", "LibraryEntryKeyExists", model.Key);
            return;
        }
        if (outcome.IsSuccess)
        {
            _newEntry = new NewEntryModel();
            _portKeyAlert = false;
            Toast.Success("Added", "EntryAdded");
            await ShowEntryAsync(outcome.Value!, clearFilters: true);
        }
    }

    // R-10 / R-16: what the server answered shows at once through the grid's row changes; the quiet read the
    // grid runs behind it corrects. The library line (who reads it) follows on its own, away from the grid.
    private Task _sideRefresh = Task.CompletedTask;

    /// <summary>The reads started by the last change, for a caller that must wait for them.</summary>
    internal Task BackgroundRefresh => Task.WhenAll(_entriesGrid?.BackgroundRefresh ?? Task.CompletedTask, _sideRefresh, _keyLinkTask);

    /// <summary>Draws <paramref name="entry"/> as the server answered it. A new entry that column filters or the
    /// page's <c>?key=</c> link would hide (R-65) clears them first, one read of the grid, before it shows.</summary>
    private async Task ShowEntryAsync(VariableEntryDto entry, bool clearFilters = false)
    {
        if (clearFilters) await ClearEntryFiltersAsync();
        if (_entriesGrid is not null) await _entriesGrid.ShowRowAsync(entry, VariableEntrySort.Order(_entrySortBy, _entrySortDescending));
        RefreshLibraryInBackground();
    }

    private async Task HideEntryAsync(VariableEntryDto entry)
    {
        if (_entriesGrid is not null) await _entriesGrid.HideRowAsync(entry);
        RefreshLibraryInBackground();
    }

    /// <summary>The library's own read (who reads it, R-64) after a change; the entries are the grid's.</summary>
    private void RefreshLibraryInBackground() =>
        _sideRefresh = InvokeAsync(async () =>
        {
            try
            {
                await ReloadDetail();
            }
            catch (HttpRequestException)
            {
                // What the line showed stays; the next read of the page brings the server's.
            }
        });

    private bool _portKeyAlert;

    private bool HasPortKey => _newEntry.Key.Trim().StartsWith(PortRegistryLimits.PortKeyPrefix, StringComparison.Ordinal);

    private bool HasNoKey => string.IsNullOrWhiteSpace(_newEntry.Key);

    private bool _portKeyHint;
    private bool _focusNewEntryKey;

    private void ClearPortKeyAlert() => (_portKeyAlert, _portKeyHint) = (false, false);

    /// <summary>The add row's "Free port": fills the Value field with a free port reserved for the typed
    /// <c>PORT_</c> key (the Add button then writes the entry). No key says to type one and focuses the field;
    /// a name without the prefix says to rename it.</summary>
    internal async Task FillFreePortAsync()
    {
        ClearPortKeyAlert();
        if (HasNoKey)
        {
            _portKeyHint = true;
            _focusNewEntryKey = true;
            return;
        }

        if (!HasPortKey)
        {
            _portKeyAlert = true;
            return;
        }

        var (port, error) = await LibraryPortTargets.TryPreviewFreeAsync(
            Api, Id!.Value, _newEntry.Key.Trim(), L["PortAllocateFailed"].Value, L["PortFreeNeedsOneServer"].Value);
        if (port is null)
        {
            Toast.ErrorRaw(L["PortFree"].Value, error!);
            return;
        }

        _newEntry.Value = port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>After an add (R-65): the grid's filters, a header filter or the page's own <c>?key=</c> link,
    /// would hide the entry just added, so they go before the entry is shown. Clearing filters is a change
    /// of view, so its single read of the grid is a normal one; with no filter nothing is read here.</summary>
    private async Task ClearEntryFiltersAsync()
    {
        if (!string.IsNullOrEmpty(FilterKey))
        {
            FilterKey = null;
            Nav.NavigateTo(Nav.GetUriWithQueryParameter("key", (string?)null), replace: true);
        }
        if (_entryFilters.Count == 0 || _entriesGrid?.Grid is not { } grid) return;
        // Clears every column filter and reads the first page again, once.
        await grid.SetFiltersAsync(new Dictionary<string, string?>(), replace: true);
    }

    // The edit button asks the grid; a click or key in a cell does it by itself (EditTrigger Cell, R-75).
    // Either way the grid reports the row through OnEntryEdit before its editors render.
    private void EditEntry(VariableEntryDto entry) => _entriesGrid?.EditRow(entry);

    private void OnEntryEdit(VariableEntryDto entry)
    {
        _editingEntry = entry;
        _editEntryKey = entry.Key;
        _editEntryValue = entry.Value;
    }

    private void OnEntryKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e, VariableEntryDto entry)
    {
        if (e.Key == "Enter")
            _entriesGrid?.UpdateRow(entry);
        else if (e.Key == "Escape")
            _entriesGrid?.CancelEditRow(entry);
    }

    // The rows being saved, by id, with the key and value just validated (R-66). The grid takes the row out of
    // edit mode the moment it is validated, before the server has answered: until the reload brings the
    // saved row back, a row showing the entry as the last load read it would put the old value back for a moment.
    private readonly Dictionary<int, VariableEntryDto> _savedRows = [];

    private VariableEntryDto Shown(VariableEntryDto entry) => _savedRows.GetValueOrDefault(entry.Id, entry);

    private async Task OnEntryUpdate(VariableEntryDto entry)
    {
        _savedRows[entry.Id] = entry with { Key = _editEntryKey, Value = _editEntryValue };
        try
        {
            var outcome = await Api.Variables.UpdateVariableEntryAsync(Id!.Value, entry.Id, new UpdateVariableEntryRequest
            {
                Key = _editEntryKey,
                Value = _editEntryValue
            });
            if (outcome.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                Toast.Error("Error", "LibraryEntryKeyExists", _editEntryKey);
                return;
            }
            if (!outcome.IsSuccess)
            {
                Toast.Error("Error", "OperationFailed");
                return;
            }
            _editingEntry = null;
            Toast.Success("Saved", "EntryUpdated");
            // The saved row is drawn from the answer; the entry of a server that answers nothing readable
            // keeps the key and value just validated.
            var saved = outcome.Value ?? entry with { Key = _editEntryKey, Value = _editEntryValue };
            await ShowEntryAsync(saved);
        }
        finally
        {
            _savedRows.Remove(entry.Id);
        }
    }

    private async Task DeleteEntry(VariableEntryDto deleted)
    {
        var confirmed = await Dialog.Confirm(L["DeleteEntryConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        // Read the port flag from the clicked row because a virtualized grid may no longer hold
        // that row in its latest fetched block.
        var status = await Veil.RunAsync(() => Api.Variables.DeleteVariableEntryAsync(Id!.Value, deleted.Id));
        if (!status.Success)
        {
            Toast.Error("Error", "OperationFailed");
            return;
        }
        Toast.Success("Deleted", "EntryDeleted");
        // The row leaves the grid at once. A PORT_* entry's reservation was released by the backend with the
        // entry (R-30), so there is nothing left to ask.
        await HideEntryAsync(deleted);
    }

    private void ResetEntryBuffers()
    {
        if (_editingEntry is not null)
            _entriesGrid?.CancelEditRow(_editingEntry);
        _editingEntry = null;
        _newEntry = new NewEntryModel();
        _editEntryKey = string.Empty;
        _editEntryValue = string.Empty;
    }

    private async Task ShowVersions(VariableEntryDto entry)
    {
        var restored = await Dialog.OpenAsync<VersionHistoryDialog>(
            string.Format(L["VersionHistory"], entry.Key),
            new Dictionary<string, object?>
            {
                { "LibraryId", Id!.Value },
                { "EntryId", entry.Id },
                { "CurrentVersion", entry.VersionCount }
            },
            new OmniDialogOptions { Width = "720px", AutoFocusFirstElement = false });
        if (restored is true) await ReadEntriesQuietlyAsync(clearFilters: false);
    }

    private Task ExportEntries() => new VariableEntryTransfer(Api, JS, Toast).ExportAsync(Id!.Value, _detail?.Name);

    /// <summary> Hands the library free ports and writes them as PORT_* entries. Reloads only when
    /// something was actually created, so a cancelled dialog costs nothing. </summary>
    private async Task AllocatePortsAsync()
    {
        var created = await OpenPortAllocateAsync(new Dictionary<string, object?> { ["LibraryId"] = Id!.Value });
        if (created) await ReadEntriesQuietlyAsync(clearFilters: false);
    }

    private async Task<bool> OpenPortAllocateAsync(Dictionary<string, object?> parameters)
    {
        var allocated = await Dialog.OpenAsync<LibraryPortAllocateDialog>(
            L["PortAllocate"].Value, parameters, new OmniDialogOptions { Width = "560px", AutoFocusFirstElement = false });
        return allocated is true;
    }

    /// <summary>A dialog (ports allocated, a version restored) hands back no rows, only that rows changed: the grid reads again,
    /// quietly, after the filters that would hide the new keys are cleared when the caller asks.</summary>
    private async Task ReadEntriesQuietlyAsync(bool clearFilters)
    {
        if (clearFilters) await ClearEntryFiltersAsync();
        if (_entriesGrid is not null) await _entriesGrid.RestoreRowsAsync();
        RefreshLibraryInBackground();
    }

    /// <summary>Only an empty <c>PORT_</c> key offers a port: one that holds a value is never overwritten
    /// by an allocation.</summary>
    private static bool CanAllocatePort(VariableEntryDto entry) =>
        entry.Key.StartsWith(PortRegistryLimits.PortKeyPrefix, StringComparison.Ordinal)
        && string.IsNullOrWhiteSpace(entry.Value);

    /// <summary>The row menu of an empty <c>PORT_</c> key (R-87): allocates that key's port. With one
    /// reachable server there is nothing to ask, so it allocates at once; with several servers, or a
    /// library that cannot allocate, the allocation dialog opens on that key and says why or asks.</summary>
    internal Task AllocateEntryPortAsync(VariableEntryDto entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return AllocatePortForKeyAsync(entry.Key, clearFilters: false);
    }

    /// <summary>Allocates the port of one key, an existing empty one or a new one (the endpoint creates
    /// the entries it lacks, so a new key costs one request). True when the key now holds its port.</summary>
    private async Task<bool> AllocatePortForKeyAsync(string key, bool clearFilters)
    {
        var targets = await LibraryPortTargets.TryLoadAsync(Api, Id!.Value);
        if (targets is null)
        {
            Toast.Error("Error", "LoadFailed");
            return false;
        }

        if (targets.Blocker != PortAllocationBlocker.None || LibraryPortTargets.InitialServerId(targets) is not { } serverId)
        {
            var allocated = await OpenPortAllocateAsync(
                new Dictionary<string, object?> { ["LibraryId"] = Id.Value, ["InitialKeys"] = new[] { key } });
            if (allocated) await ReadEntriesQuietlyAsync(clearFilters);
            return allocated;
        }

        var (written, error) = await LibraryPortTargets.TryAllocateAsync(Api, Id.Value, serverId, key, L["PortAllocateFailed"].Value);
        if (written is null)
        {
            Toast.ErrorRaw(L["PortAllocate"].Value, error!);
            return false;
        }

        Toast.Success("PortAllocated", "PortAllocatedKey", written.Key, written.Value);
        await ShowEntryAsync(written, clearFilters);
        return true;
    }

    /// <summary>
    /// Checks the library's own PORT_* values against a server, crossing the registry with what the
    /// agent last saw listening. Pre-filled from the library, so the operator does not retype ports
    /// that are already written down.
    /// </summary>
    private async Task CheckPortsAsync()
    {
        var moved = await Dialog.OpenAsync<LibraryPortCheckDialog>(
            L["CheckPorts"].Value,
            new Dictionary<string, object?> { ["LibraryId"] = Id!.Value },
            new OmniDialogOptions { Width = "620px", AutoFocusFirstElement = false });
        if (moved is true) await ReadEntriesQuietlyAsync(clearFilters: false);
    }

    private async Task ImportEntries()
    {
        var json = await Dialog.OpenAsync<ImportJsonDialog>(
            L["ImportEntries"].Value,
            new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });

        if (json is not string content || string.IsNullOrWhiteSpace(content)) return;

        if (await new VariableEntryTransfer(Api, JS, Toast).ImportAsync(Id!.Value, content))
            await ReloadEntriesAsync();
    }

    private async Task ReloadDetail()
    {
        _detail = await Api.Variables.GetVariableLibraryDetailAsync(Id!.Value);
        // R-64: the page's own save (a rename, an entry) can change who reads the library.
        await RefreshUsageAsync();
    }

    private async Task LoadEntriesAsync(GridLoadArgs args)
    {
        if (Id is null or 0) return;
        _entryPageSize = args.Top ?? 25;
        _entryPage = ((args.Skip ?? 0) / _entryPageSize) + 1;
        (_entrySortBy, _entrySortDescending) = VariableEntrySort.Read(args);
        // Kept with the page and sort, so the reload after an edit shows the same rows.
        _entryFilters = args.ToApiFilters();
        await LoadEntryPageAsync();
    }

    /// <summary>One page of the entries export (a library holds plain values, secrets live in
    /// vaults), with the grid's sort and column filters.</summary>
    private Task<PaginatedResult<VariableEntryDto>> ReadEntriesExportAsync(GridLoadArgs args)
    {
        if (Id is null or 0) return Task.FromResult(new PaginatedResult<VariableEntryDto>());
        var (page, pageSize) = args.ToPageRequest(25);
        var (sortBy, sortDescending) = VariableEntrySort.Read(args);
        return Api.Variables.GetVariableEntriesPageAsync(Id.Value, page, pageSize,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
    }

    private const string NewEntryValueId = "library-new-entry-value";
    private const string NewEntryKeyId = "library-new-entry-key";

    /// <summary>R-56: a <c>?key=</c> link to an entry that does not exist yet (the project overview's missing
    /// variables) opens the add form with that key filled and the focus on the value, rather than leaving an
    /// empty filtered grid. Decided once, on the first read that answers; a key that exists keeps its filter.</summary>
    private bool _keyLinkResolved;
    private bool _focusNewEntryValue;
    private Task _keyLinkTask = Task.CompletedTask;

    private void ResolveKeyLink()
    {
        if (_keyLinkResolved || _entriesLoadFailed || string.IsNullOrWhiteSpace(FilterKey)) return;
        _keyLinkResolved = true;
        var key = FilterKey.Trim();
        // More rows than the page holds: the exact key may be on a later page, so the filter stays.
        if (_entryCount > _entries.Count || _entries.Exists(entry => string.Equals(entry.Key, key, StringComparison.Ordinal))) return;
        _newEntry.Key = key;
        _focusNewEntryValue = true;
        _keyLinkTask = InvokeAsync(async () =>
        {
            await ClearEntryFiltersAsync();
            StateHasChanged();
        });
    }

    private async Task LoadEntryPageAsync()
    {
        if (Id is null or 0) return;
        _entriesLoading = true;
        _entriesLoadFailed = false;
        try
        {
            var result = await Api.Variables.GetVariableEntriesPageAsync(
                Id.Value, _entryPage, _entryPageSize,
                sortBy: _entrySortBy, sortDescending: _entrySortDescending, filters: _entryFilters);
            _entries = result.Items;
            _entryCount = result.TotalCount;
            ResolveKeyLink();
        }
        catch (HttpRequestException)
        {
            _entriesLoadFailed = true;
        }
        finally
        {
            _entriesLoading = false;
        }
    }

    private async Task ReloadEntriesAsync()
    {
        await ReloadDetail();
        // The grid scrolls (remote virtualization) and only shows what it fetched itself; it
        // fetches the rows on screen again, with its sort and filters, and sets the count.
        await (_entriesGrid is not null ? _entriesGrid.Refresh() : LoadEntryPageAsync());
    }

    private Task RetryEntriesAsync() => _entriesGrid?.Reload() ?? Task.CompletedTask;

    private sealed class NewEntryModel
    {
        [Required, StringLength(200)]
        public string Key { get; set; } = string.Empty;

        [StringLength(10_000)]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class LibraryModel : ScopedResourceFormModel
    {
    }

    /// <summary>The export writes the count the badge shows.</summary>
    private static readonly Func<VariableEntryDto, object?> VersionsExport = entry => entry.VersionCount;
}
