// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Front.Components.Pipelines;
namespace Aetheus.Front.Components.VariableLibraries;

public partial class VariableLibraryEdit
{
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

        // Publish the parent project so the NavMenu keeps the project's submenu open
        // while we're editing one of its variable libraries.
        ProjectNav.Set(_model.ProjectId);
        ReassertBreadcrumb(isNew);
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
                    Nav.NavigateTo($"/variable-libraries/{created.Id}");
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

        var status = await Api.Variables.DeleteVariableLibraryAsync(Id!.Value);
        if (!status.Success)
        {
            Toast.Error("Error", "OperationFailed");
            return;
        }
        Cache.InvalidatePrefix("variable-libraries:"); // S-TECH-SWIV
        Toast.Success("Deleted", "Deleted");
        Nav.NavigateTo("/variable-libraries");
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
            Toast.Success("Added", "EntryAdded");
            await ReloadEntriesAsync();
        }
    }

    private void EditEntry(VariableEntryDto entry)
    {
        _editingEntry = entry;
        _editEntryKey = entry.Key;
        _editEntryValue = entry.Value;
        _entriesGrid?.EditRow(entry);
    }

    private void OnEditableCellKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e, VariableEntryDto entry)
    {
        if (e.Key is "Enter" or " " or "Spacebar")
            EditEntry(entry);
    }

    private void OnEntryKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e, VariableEntryDto entry)
    {
        if (e.Key == "Enter")
            _entriesGrid?.UpdateRow(entry);
        else if (e.Key == "Escape")
            _entriesGrid?.CancelEditRow(entry);
    }

    private async Task OnEntryUpdate(VariableEntryDto entry)
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
        await ReloadEntriesAsync();
    }

    private async Task DeleteEntry(VariableEntryDto deleted)
    {
        var confirmed = await Dialog.Confirm(L["DeleteEntryConfirm"].Value, L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        // Read the port flag from the clicked row because a virtualized grid may no longer hold
        // that row in its latest fetched block.
        var status = await Api.Variables.DeleteVariableEntryAsync(Id!.Value, deleted.Id);
        if (!status.Success)
        {
            Toast.Error("Error", "OperationFailed");
            return;
        }
        Toast.Success("Deleted", "EntryDeleted");
        await OfferPortReleaseAsync(deleted);
        await ReloadEntriesAsync();
    }

    /// <summary> A deleted <c>PORT_*</c> entry leaves its reservation behind, which would keep the
    /// port blocked for everybody with nothing using it. The release is offered, not automatic, and
    /// the backend only ever releases a reservation held by this library's own project. </summary>
    internal async Task OfferPortReleaseAsync(VariableEntryDto? deleted)
    {
        if (deleted is null) return;
        if (!deleted.Key.StartsWith(PortRegistryLimits.PortKeyPrefix, StringComparison.Ordinal)) return;
        if (!int.TryParse(deleted.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > 65535)
        {
            return;
        }

        var confirmed = await Dialog.Confirm(
            string.Format(CultureInfo.CurrentCulture, L["PortReleaseAfterDeleteConfirm"].Value, port),
            L["PortRelease"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["PortRelease"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed != true) return;

        try
        {
            var released = await Api.Variables.ReleaseLibraryPortAsync(Id!.Value, port);
            // Zero means the port is held by somebody else, which the library must not release; saying
            // so is more useful than a success toast for something that did not happen.
            if (released > 0) Toast.Success("PortReservationReleased");
            else Toast.Warning("PortRelease", "PortReleaseNotOurs", port);
        }
        catch (HttpRequestException)
        {
            Toast.Error("PortReservationFailed");
        }
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
        if (restored is true) await ReloadEntriesAsync();
    }

    private async Task ExportEntries()
    {
        var entries = await Api.Variables.ExportVariableEntriesAsync(Id!.Value);
        var json = JsonSerializer.Serialize(entries.Select(e => new { e.Key, e.Value }), new JsonSerializerOptions { WriteIndented = true });
        await JS.InvokeVoidAsync("downloadFile", $"{_detail?.Name ?? "entries"}.json", json, "application/json");
        Toast.Success("Exported", "EntriesExported", entries.Count);
    }

    /// <summary> Hands the library free ports and writes them as PORT_* entries. Reloads only when
    /// something was actually created, so a cancelled dialog costs nothing. </summary>
    private async Task AllocatePortsAsync()
    {
        var created = await Dialog.OpenAsync<LibraryPortAllocateDialog>(
            L["PortAllocate"].Value,
            new Dictionary<string, object?> { ["LibraryId"] = Id!.Value },
            new OmniDialogOptions { Width = "560px", AutoFocusFirstElement = false });

        if (created is true) await ReloadEntriesAsync();
    }

    /// <summary>
    /// Checks the library's own PORT_* values against a server, crossing the registry with what the
    /// agent last saw listening. Pre-filled from the library, so the operator does not retype ports
    /// that are already written down.
    /// </summary>
    private Task CheckPortsAsync() => Dialog.OpenAsync<LibraryPortCheckDialog>(
        L["CheckPorts"].Value,
        new Dictionary<string, object?> { ["LibraryId"] = Id!.Value },
        new OmniDialogOptions { Width = "620px", AutoFocusFirstElement = false });

    private async Task ImportEntries()
    {
        var json = await Dialog.OpenAsync<ImportJsonDialog>(
            L["ImportEntries"].Value,
            new Dictionary<string, object?>(),
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });

        if (json is not string content || string.IsNullOrWhiteSpace(content)) return;

        List<CreateVariableEntryRequest>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<CreateVariableEntryRequest>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            Toast.Error("Error", "InvalidJsonFormat");
            return;
        }

        if (entries is null or { Count: 0 }) return;

        var result = await Api.Variables.ImportVariableEntriesAsync(Id!.Value, entries);
        if (result is not null)
        {
            Toast.Success("Imported", "EntriesImported", result.ImportedCount);
            await ReloadEntriesAsync();
        }
    }

    private async Task ReloadDetail()
    {
        _detail = await Api.Variables.GetVariableLibraryDetailAsync(Id!.Value);
    }

    private async Task LoadEntriesAsync(GridLoadArgs args)
    {
        if (Id is null or 0) return;
        _entryPageSize = args.Top ?? 25;
        _entryPage = ((args.Skip ?? 0) / _entryPageSize) + 1;
        (_entrySortBy, _entrySortDescending) = ResolveEntrySort(args);
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
        var (sortBy, sortDescending) = ResolveEntrySort(args);
        return Api.Variables.GetVariableEntriesPageAsync(Id.Value, page, pageSize,
            sortBy: sortBy, sortDescending: sortDescending, filters: args.ToApiFilters());
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

    private Task RetryEntriesAsync()
        => _entriesGrid?.Reload() ?? Task.CompletedTask;

    private static (string SortBy, bool Descending) ResolveEntrySort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return ("Key", false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
    }

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
