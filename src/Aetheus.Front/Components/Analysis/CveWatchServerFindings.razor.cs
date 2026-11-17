// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Analysis;

/// <summary>R-41: the host findings of the servers in the CVE watch tab, read page by page through the caller's reach.</summary>
public partial class CveWatchServerFindings : ComponentBase
{
    internal const int PageSize = 25;

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public AnalysisSeverity? Severity { get; set; }
    [Parameter] public int? OrganizationId { get; set; }
    [Parameter] public bool ExploitedOnly { get; set; }
    [Parameter] public bool WithoutFixOnly { get; set; }

    private List<ServerHostFindingDto> _items = [];
    private (AnalysisSeverity?, int?, bool, bool)? _loadedFilter;
    private int _total;
    private int _page = 1;
    private bool _busy;
    private bool _loaded;
    private bool _loadFailed;

    protected override async Task OnParametersSetAsync()
    {
        var filter = (Severity, OrganizationId, ExploitedOnly, WithoutFixOnly);
        if (_loadedFilter == filter) return;
        _loadedFilter = filter;
        _page = 1;
        await LoadAsync();
    }

    private async Task OnPageChangedAsync(int page)
    {
        _page = page;
        await LoadAsync();
    }

    internal async Task LoadAsync()
    {
        _busy = true;
        _loadFailed = false;
        try
        {
            var result = await Api.Security.GetHostFindingsAsync(_page, PageSize, Severity, ExploitedOnly, WithoutFixOnly, OrganizationId);
            _items = result.Items;
            _total = result.TotalCount;
            _loaded = true;
        }
        catch (HttpRequestException)
        {
            _items = [];
            _total = 0;
            _loadFailed = true;
        }
        finally
        {
            _busy = false;
        }
    }
}
