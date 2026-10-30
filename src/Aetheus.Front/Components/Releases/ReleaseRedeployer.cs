// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Components.Releases;

/// <summary>
/// R-72: deploys a release again, or goes back to an older one, from the release list and the release page. No
/// pipeline of its own: it opens the launch of the project's production deployment pipeline with the version
/// filled in, so the user confirms with one click; what the quality gate holds against the release is confirmed
/// in one dialog and overridden (R-72), the artifact check and the pipeline's approvals still decide.
/// A deployment pipeline is the one of the project that declares the <c>candidateVersion</c> parameter, the rule
/// the control plane applies to a run to read the Gate's verdict on the release. When the project has several,
/// the user picks one first.
/// </summary>
internal sealed class ReleaseRedeployer(
    ApiClient api,
    PipelineRunGate runGate,
    PipelineRunDialogCoordinator runDialogs,
    OmniDialogService dialog,
    NotifyHelper toast,
    NavigationManager navigation,
    IStringLocalizer<AppStrings> localizer)
{
    public async Task<bool> RedeployAsync(ReleaseDto release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!ReleaseRedeploy.CanRedeploy(release)) return false;

        // The pipeline that deployed the previous release is already known to the list; otherwise ask the project.
        var pipelineId = release.RedeployPipelineId ?? await ChooseDeployPipelineAsync(release.ProjectId);
        if (pipelineId is not { } id) return false;

        var prefill = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ReleaseRedeploy.VersionParameter] = release.Version
        };
        // R-72 (owner decision of 2026-10-28): a release that keeps its artifact can always be deployed again;
        // what the quality gate holds against it is confirmed in one dialog, then the launch overrides the gate.
        var launcher = new PipelineRunLauncher(api, runGate, runDialogs, navigation, toast, localizer) { OfferGateOverride = true };
        return await launcher.LaunchAsync(id, sourceBranch: null, prefill) is not null;
    }

    private async Task<int?> ChooseDeployPipelineAsync(int projectId)
    {
        var pipelines = await api.Pipelines.GetPipelinesAsync(pageSize: 100, projectId: projectId);
        var takesAVersion = await Task.WhenAll(pipelines.Items.Select(pipeline => TakesAVersionAsync(pipeline.Id)));
        var deployments = pipelines.Items.Where((_, index) => takesAVersion[index]).ToList();
        switch (deployments.Count)
        {
            case 0:
                toast.Warning("NoDeployPipelineFound", "NoDeployPipelineFound");
                return null;
            case 1:
                return deployments[0].Id;
        }

        var selected = await dialog.OpenAsync<PipelineSelectDialog>(
            localizer["Redeploy"].Value,
            new Dictionary<string, object?> { { "Pipelines", deployments } },
            new OmniDialogOptions { Width = "400px", AutoFocusFirstElement = false });
        return selected as int?;
    }

    /// <summary>Whether the pipeline declares the version parameter. A pipeline whose declaration cannot be read
    /// (an invalid definition, no answer) is not a candidate: the choice never stops on a pipeline the user did
    /// not ask about, and the read is silent for the same reason.</summary>
    private async Task<bool> TakesAVersionAsync(int pipelineId)
    {
        try
        {
            var outcome = await api.Pipelines.GetPipelineRunParametersOutcomeAsync(pipelineId);
            return outcome.Value?.Any(parameter => string.Equals(
                parameter.Name, ReleaseRedeploy.VersionParameter, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception exception) when (RunParametersReading.IsTransportFailure(exception))
        {
            return false;
        }
    }
}
