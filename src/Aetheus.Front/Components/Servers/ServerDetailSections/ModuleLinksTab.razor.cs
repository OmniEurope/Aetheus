// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ModuleLinksTab
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }
    [Parameter, EditorRequired] public ModuleLinkType SourceType { get; set; }
    [Parameter, EditorRequired] public List<string> Resources { get; set; } = [];

    private List<LinkedResourceDto> _links = [];
    private AetheusDataGrid<LinkedResourceDto>? _grid;
    private int _totalCount;
    private int _page = 1;
    private string? _loadedKey;
    private int _pageSize = 25;
    private string _sortBy = "Identifier";
    private bool _sortDescending;
    private string? _search;
    private bool _loading = true;
    private bool _detecting;

    private ModuleLinkType[] _targetTypes = [];
    private List<Aetheus.Shared.Components.Shared.GridFilter> _columnFilters = [];
    private Func<string, string>? _typeText;
    private Func<string, string>? _sourceText;

    // Recette R-210: what the Type and Source header filters show for each value.
    private Func<string, string> TypeText => _typeText ??= GridFilterText.ForEnum<ModuleLinkType>(L);
    private Func<string, string> SourceText => _sourceText ??= value =>
        bool.TryParse(value, out var automatic) ? L[automatic ? "Auto" : "Manual"].Value : value;

    protected override async Task OnParametersSetAsync()
    {
        _targetTypes = Enum.GetValues<ModuleLinkType>().Where(t => t != SourceType).ToArray();
        // Recette R-327: the grid scrolls (remote virtualization) and only shows what it fetched itself. A
        // parent render with the same server, type and resources leaves it, and its scroll position, alone;
        // a real change reloads it from its first row.
        var key = $"{ServerId}|{SourceType}|{string.Join('\n', Resources)}";
        if (key == _loadedKey) return;
        _loadedKey = key;
        await ReloadLinksAsync();
    }

    private Task ReloadLinksAsync()
    {
        _page = 1;
        return _grid is not null ? _grid.GoToPage(0, forceReload: true) : LoadLinksAsync();
    }

    /// <summary>Recette R2-124: one page of the export, the same query as the grid's load.</summary>
    private Task<PaginatedResult<LinkedResourceDto>> ReadExportAsync(GridLoadArgs args)
    {
        var resourceIdentifiers = Resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (resourceIdentifiers.Count == 0) return Task.FromResult(new PaginatedResult<LinkedResourceDto>());
        var (page, pageSize) = args.ToPageRequest();
        var (sortBy, sortDescending) = GetSort(args);
        return Api.Servers.GetModuleLinksPageAsync(ServerId, new ModuleLinkPageRequest
        {
            SourceType = SourceType,
            ResourceIdentifiers = resourceIdentifiers,
            Page = page,
            PageSize = pageSize,
            Search = _search,
            SortBy = sortBy,
            SortDescending = sortDescending,
            Filters = args.ToApiFilters()
        });
    }

    private async Task LoadLinksAsync()
    {
        _loading = true;
        try
        {
            var resourceIdentifiers = Resources
                .Where(resource => !string.IsNullOrWhiteSpace(resource))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (resourceIdentifiers.Count == 0)
            {
                _links = [];
                _totalCount = 0;
                return;
            }

            var result = await Api.Servers.GetModuleLinksPageAsync(ServerId, new ModuleLinkPageRequest
            {
                SourceType = SourceType,
                ResourceIdentifiers = resourceIdentifiers,
                Page = _page,
                PageSize = _pageSize,
                Search = _search,
                SortBy = _sortBy,
                SortDescending = _sortDescending,
                Filters = _columnFilters
            });
            _links = result.Items;
            _totalCount = result.TotalCount;
        }
        catch
        {
            Toast.Error(L["LoadFailed"]);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnLoadDataAsync(GridLoadArgs args)
    {
        (_page, _pageSize) = args.ToPageRequest();
        (_sortBy, _sortDescending) = GetSort(args);
        // Recette R-210: the header filters, applied by the API.
        _columnFilters = args.ToApiFilters();
        await LoadLinksAsync();
    }

    private Task OnSearchChangedAsync() => ReloadLinksAsync();

    private async Task AutoDetectAsync()
    {
        _detecting = true;
        try
        {
            var success = await Api.Servers.AutoDetectModuleLinksAsync(ServerId);
            if (success)
            {
                Toast.Success(L["AutoDetectComplete"]);
                await ReloadLinksAsync();
            }
            else
            {
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch
        {
            Toast.Error(L["ActionFailed"]);
        }
        finally
        {
            _detecting = false;
        }
    }

    private async Task OpenAddDialogAsync()
    {
        var dialogResult = await Dialog.OpenAsync<AddModuleLinkDialog>(
            L["AddLink"].Value,
            new Dictionary<string, object?>
            {
                { "TargetTypes", _targetTypes },
                { "Resources", Resources }
            },
            new OmniDialogOptions { Width = "400px", AutoFocusFirstElement = false });

        if (dialogResult is AddModuleLinkResult result)
        {
            await AddLinkAsync(result);
        }
    }

    private async Task AddLinkAsync(AddModuleLinkResult request)
    {
        try
        {
            var result = await Api.Servers.CreateModuleLinkAsync(ServerId, new CreateModuleLinkRequest
            {
                SourceType = SourceType,
                SourceIdentifier = request.SourceIdentifier,
                TargetType = request.TargetType,
                TargetIdentifier = request.TargetIdentifier
            });
            if (result is not null)
            {
                Toast.Success(L["LinkCreated"]);
                await ReloadLinksAsync();
            }
            else
            {
                Toast.Error(L["ActionFailed"]);
            }
        }
        catch
        {
            Toast.Error(L["ActionFailed"]);
        }
    }

    private async Task DeleteLinkAsync(int linkId)
    {
        var success = await Api.Servers.DeleteModuleLinkAsync(ServerId, linkId);
        if (success)
        {
            Toast.Success(L["LinkDeleted"]);
            await ReloadLinksAsync();
        }
        else
        {
            Toast.Error(L["ActionFailed"]);
        }
    }

    internal static OmniTone GetTypeBadge(ModuleLinkType type) => type switch
    {
        ModuleLinkType.Docker => OmniTone.Accent,
        ModuleLinkType.Apache => OmniTone.Warning,
        ModuleLinkType.Certbot => OmniTone.Success,
        _ => OmniTone.Neutral
    };

    private static (string SortBy, bool Descending) GetSort(GridLoadArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OrderBy)) return ("Identifier", false);
        var parts = args.OrderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], parts.Length > 1
            && string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase));
    }
}
