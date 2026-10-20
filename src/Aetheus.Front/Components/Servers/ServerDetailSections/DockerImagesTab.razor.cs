// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// The Docker images tab: the inventory grid, the pull box and the build card.
///
/// Unlike <see cref="DockerNetworksTab"/> this one writes, so it takes <see cref="ServerId"/> and calls
/// the API itself. It still never mutates <see cref="Images"/>: pull, delete and build all queue a task
/// on the agent, and the refreshed inventory comes back through the parent, which stays the single owner
/// of the list. Every field here is view state (search text, the two build inputs, the busy flags), and
/// the parent's <c>@key="ServerId"</c> is what clears it when the server changes.
/// </summary>
public partial class DockerImagesTab
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int ServerId { get; set; }

    /// <summary>BACK-012: the backend takes Server.Admin for this tab's agent-reaching writes (compose
    /// deploy, image build and delete, container shell); without it they are not offered.</summary>
    [Parameter] public bool CanAdmin { get; set; }

    private ServerTaskFollower? _follower;
    [Parameter, EditorRequired] public IReadOnlyList<DockerImageDto> Images { get; set; } = [];

    // SITE-OFFLINE-001: every action of this tab queues a task only the agent can claim. The parent passes
    // no server, so its agent's presence comes from the loader the server detail layout cascades.
    [CascadingParameter(Name = "ServerLoader")] public ServerDetailLoader? Loader { get; set; }

    private bool AgentOffline => Loader?.Server is { } server && AgentOfflineGuard.IsOffline(server);
    private bool BlockedByOfflineAgent() => Loader?.Server is { } server && AgentOfflineGuard.Blocks(server, Toast);

    private string _imageSearch = string.Empty;

    // Each heartbeat brings a new image list; images that were not there read bold.
    private OmniDataGrid<DockerImageDto>? _grid;
    private readonly LiveGridRows _liveRows = new();

    protected override Task OnParametersSetAsync() => _liveRows.ObserveAsync(_grid, Images);

    private string _pullImageName = string.Empty;
    private bool _imagePulling;

    private string _buildImageTag = string.Empty;
    private string _buildDockerfileContent = string.Empty;
    private bool _buildingImage;

    private IReadOnlyList<OmniDataGridGroup> _imageGroups =
    [
        new(nameof(DockerImageDto.Project), false)
    ];

    // Open the inventory on its rows rather than collapsing its project groups.
    private bool _allImageGroupsExpanded = true;

    private List<DockerImageDto> FilteredImages => Images
        .Where(i => string.IsNullOrWhiteSpace(_imageSearch)
            || i.Repository.Contains(_imageSearch, StringComparison.OrdinalIgnoreCase)
            || i.Tag.Contains(_imageSearch, StringComparison.OrdinalIgnoreCase))
        .ToList();

    private async Task PullImageAsync()
    {
        if (BlockedByOfflineAgent()) return;
        if (string.IsNullOrWhiteSpace(_pullImageName)) return;
        _imagePulling = true;
        var request = new DockerPullImageRequest { Image = _pullImageName.Trim() };
        // Recette R3-37: the follower shows what becomes of the task, in place of a lone "queued" toast.
        _follower?.Expect($"{L["DockerPull"]} {request.Image}");
        var success = await Api.ServerTools.PullDockerImageAsync(ServerId, request);
        if (success)
        {
            Toast.Success("Docker", "DockerPullQueued", _pullImageName);
            _pullImageName = string.Empty;
        }
        else
        {
            _follower?.Forget();
            Toast.Error("Docker", "DockerPullFailed");
        }
        _imagePulling = false;
    }

    private async Task ConfirmRemoveImageAsync(DockerImageDto image)
    {
        if (BlockedByOfflineAgent()) return;
        var label = string.IsNullOrWhiteSpace(image.Repository)
            ? image.ImageId[..Math.Min(12, image.ImageId.Length)]
            : $"{image.Repository}:{image.Tag}";
        var confirmed = await Dialog.Confirm(
            string.Format(L["DockerRemoveImageConfirm"].Value, label),
            L["Delete"].Value,
            new OmniConfirmOptions { Destructive = true, OkButtonText = L["Delete"].Value, CancelButtonText = L["GoBack"].Value });
        if (confirmed == true)
            await RemoveImageAsync(image.ImageId);
    }

    private async Task RemoveImageAsync(string imageId)
    {
        if (BlockedByOfflineAgent()) return;
        _follower?.Expect($"{L["Delete"]} {imageId[..Math.Min(12, imageId.Length)]}");
        var success = await Api.ServerTools.RemoveDockerImageAsync(ServerId, imageId);
        if (success)
        {
            Toast.Success("Docker", "DockerRemoveImageQueued", imageId[..Math.Min(12, imageId.Length)]);
        }
        else
        {
            _follower?.Forget();
            Toast.Error("Docker", "DockerRemoveImageFailed");
        }
    }

    private async Task BuildImageAsync()
    {
        if (BlockedByOfflineAgent()) return;
        if (string.IsNullOrWhiteSpace(_buildImageTag) || string.IsNullOrWhiteSpace(_buildDockerfileContent)) return;
        _buildingImage = true;
        var request = new DockerBuildRequest
        {
            ImageTag = _buildImageTag.Trim(),
            DockerfileContent = _buildDockerfileContent
        };
        _follower?.Expect($"{L["DockerBuild"]} {request.ImageTag}");
        var success = await Api.ServerTools.BuildImageAsync(ServerId, request);
        if (success)
        {
            Toast.Success("DockerBuild", "DockerBuildQueued", _buildImageTag);
            _buildImageTag = string.Empty;
            _buildDockerfileContent = string.Empty;
        }
        else
        {
            _follower?.Forget();
            Toast.Error("DockerBuild", "DockerBuildFailed");
        }
        _buildingImage = false;
    }

    /// <summary>The export writes the whole image id, the cell its first twelve
    /// characters.</summary>
    private static readonly Func<DockerImageDto, object?> IdExport = image => image.ImageId;
}
