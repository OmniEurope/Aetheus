// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// The Docker volumes grid. Read-only over the parent's inventory, with only its own filter state -
/// see <see cref="DockerNetworksTab"/> for why the ownership runs that way.
/// </summary>
public partial class DockerVolumesTab
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<DockerVolumeDto> Volumes { get; set; } = [];

    private string _volumeSearch = string.Empty;

    // Each heartbeat brings a new volume list; volumes that were not there read bold.
    private OmniDataGrid<DockerVolumeDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Volumes);

    private IReadOnlyList<OmniDataGridGroup> _volumeGroups =
    [
        new(nameof(DockerVolumeDto.Project), false)
    ];

    // Open the inventory on its rows rather than collapsing its project groups.
    private bool _allVolumeGroupsExpanded = true;

    private List<DockerVolumeDto> FilteredVolumes => Volumes
        .Where(v => string.IsNullOrWhiteSpace(_volumeSearch)
            || v.Name.Contains(_volumeSearch, StringComparison.OrdinalIgnoreCase)
            || v.Driver.Contains(_volumeSearch, StringComparison.OrdinalIgnoreCase))
        .ToList();
}
