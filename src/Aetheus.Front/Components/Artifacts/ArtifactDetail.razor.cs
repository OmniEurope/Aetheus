// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Artifacts;

public partial class ArtifactDetail
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private ProjectNavContextService ProjectNav { get; set; } = default!;
    [Inject] private BreadcrumbService Breadcrumb { get; set; } = default!;

    [Parameter] public int ArtifactId { get; set; }

    private PipelineArtifactDto? _artifact;
    private int? _loadedArtifactId;
    private bool _loading;
    private bool _downloading;

    // The Distribution panel groups where the artifact went (releases / environment / project).
    // Hide it entirely when the artifact has none of those, so we never render an empty panel.
    private bool HasDistribution =>
        _artifact is not null &&
        (_artifact.Releases.Count > 0 || !string.IsNullOrEmpty(_artifact.EnvironmentName) || _artifact.ProjectId.HasValue);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedArtifactId == ArtifactId) return;
        _loadedArtifactId = ArtifactId;
        var artifactId = ArtifactId;
        _loading = true;
        PipelineArtifactDto? artifact;
        try { artifact = await Api.ServerTools.GetArtifactAsync(artifactId); }
        catch (HttpRequestException) { artifact = null; }
        if (ArtifactId != artifactId) return;
        _artifact = artifact;
        if (_artifact?.ProjectId is not null)
            ProjectNav.Set(_artifact.ProjectId.Value);
        ReassertBreadcrumb();
        _loading = false;
    }

    private void ReassertBreadcrumb()
    {
        if (_artifact is null) return;
        var current = new BreadcrumbItem(_artifact.Name);
        if (_artifact.ProjectId is { } projectId)
        {
            Breadcrumb.Set(
                new BreadcrumbItem(L["Projects"], "/projects"),
                new BreadcrumbItem(_artifact.ProjectName ?? $"{L["Project"]} #{projectId}", $"/projects/{projectId}/overview"),
                new BreadcrumbItem(L["Artifacts"], $"/projects/{projectId}/artifacts"),
                current);
            return;
        }
        Breadcrumb.Set(new BreadcrumbItem(L["Artifacts"]), current);
    }

    private async Task DownloadAsync()
    {
        if (_artifact is null || _downloading) return;
        _downloading = true;
        StateHasChanged();
        try
        {
            var stream = await Api.ServerTools.DownloadArtifactAsync(_artifact.Id);
            if (stream is not null)
            {
                using var streamRef = new DotNetStreamReference(stream);
                await Js.InvokeVoidAsync("downloadFileFromStream", $"{_artifact.Name}.zip", streamRef);
            }
        }
        finally
        {
            _downloading = false;
            StateHasChanged();
        }
    }

    private Task CopyPathAsync() =>
        _artifact is null ? Task.CompletedTask : Clipboard.CopyAsync(_artifact.FilePath, _artifact.FilePath);

    private Task CopyChecksumAsync() =>
        string.IsNullOrEmpty(_artifact?.Sha256) ? Task.CompletedTask : Clipboard.CopyAsync(_artifact.Sha256, _artifact.Sha256);

    private static OmniTone GetRetentionBadge(ArtifactRetentionPolicy policy) => policy switch
    {
        ArtifactRetentionPolicy.Released => OmniTone.Success,
        ArtifactRetentionPolicy.Deployed => OmniTone.Warning,
        _ => OmniTone.Neutral
    };

    private static string BranchHref(BranchLinkDto branch) => $"/git-repositories/branches/{branch.Id}";

    private string BranchHref(string branchName) => _artifact?.SourceRepositoryId is { } repositoryId
        ? $"/git-repositories/{repositoryId}?tab=branches&branch={Uri.EscapeDataString(branchName)}"
        : $"/git-repositories?projectId={_artifact?.ProjectId}";

    private string CommitHref(CommitLinkDto commit) => _artifact?.SourceRepositoryId is { } repositoryId
        ? $"/git-repositories/{repositoryId}/commits/{Uri.EscapeDataString(commit.Sha)}"
        : $"/git-repositories/commits/{commit.Id}";

    private string CommitHref(string commitHash) => _artifact?.SourceRepositoryId is { } repositoryId
        ? $"/git-repositories/{repositoryId}?tab=commits&search={Uri.EscapeDataString(commitHash)}"
        : $"/git-repositories?projectId={_artifact?.ProjectId}";
}
