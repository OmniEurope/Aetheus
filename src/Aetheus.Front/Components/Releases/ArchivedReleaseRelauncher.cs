// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Components.Releases;

/// <summary>
/// Decision D2: an archived release lost its artifacts and runs but kept its commit. The release list and
/// the release page offer the same action: choose one of the project's pipelines, run it again from that
/// commit, then open the new run. The backend pins the run to the commit and refuses any other release.
/// </summary>
internal sealed class ArchivedReleaseRelauncher(
    ApiClient api, OmniDialogService dialog, NotifyHelper toast, NavigationManager navigation, IStringLocalizer<AppStrings> localizer)
{
    public async Task<bool> RelaunchAsync(ReleaseDto release)
    {
        if (!ReleaseRelaunch.CanRelaunch(release.Status, release.CommitHash)) return false;
        var pipelines = await api.Pipelines.GetPipelinesAsync(pageSize: 100, projectId: release.ProjectId);
        if (pipelines.Items.Count == 0)
        {
            toast.Warning("NoPipelinesFound", "NoPipelinesFound");
            return false;
        }

        var selected = await dialog.OpenAsync<PipelineSelectDialog>(
            localizer["RelaunchFromCommit"].Value,
            new Dictionary<string, object?> { { "Pipelines", pipelines.Items } },
            new OmniDialogOptions { Width = "400px", AutoFocusFirstElement = false });
        if (selected is not int pipelineId) return false;

        var run = await api.Projects.RelaunchReleaseFromCommitAsync(
            release.Id, new TriggerReleaseBuildRequest { PipelineId = pipelineId });
        if (run is null) return false;
        toast.Success("RelaunchFromCommit", "RelaunchTriggered", release.Version);
        navigation.NavigateTo($"/pipelines/runs/{run.Id}");
        return true;
    }
}
