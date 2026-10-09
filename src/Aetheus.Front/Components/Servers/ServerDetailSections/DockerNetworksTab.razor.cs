// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// The Docker networks grid.
///
/// It reads <see cref="Networks"/> and never mutates it: the parent owns the refresh cycle and the
/// SignalR wiring, so a tab that wrote back would have two owners for one list. Its search text and
/// group expansion are local by design - they are view state nobody else needs, and keeping them here
/// is what let the parent shed five tabs' worth of fields.
/// </summary>
public partial class DockerNetworksTab
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<DockerNetworkDto> Networks { get; set; } = [];

    private string _networkSearch = string.Empty;

    // Recette R-227: each heartbeat brings a new network list; networks that were not there read bold.
    private OmniDataGrid<DockerNetworkDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Networks);

    private IReadOnlyList<OmniDataGridGroup> _networkGroups =
    [
        new(nameof(DockerNetworkDto.Project), false)
    ];

    // Open the inventory on its rows rather than collapsing its project groups.
    private bool _allNetworkGroupsExpanded = true;

    private List<DockerNetworkDto> FilteredNetworks => Networks
        .Where(n => string.IsNullOrWhiteSpace(_networkSearch)
            || n.Name.Contains(_networkSearch, StringComparison.OrdinalIgnoreCase)
            || n.Driver.Contains(_networkSearch, StringComparison.OrdinalIgnoreCase))
        .ToList();

    /// <summary>Recette R2-124: the export writes the whole network id, the cell its first twelve characters.</summary>
    private static readonly Func<DockerNetworkDto, object?> IdExport = network => network.NetworkId;
}
