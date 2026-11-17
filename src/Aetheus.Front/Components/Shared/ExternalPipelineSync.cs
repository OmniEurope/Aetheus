// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// R-11: the "Synchroniser" action of an imported (read-only) pipeline. It reads the mirror's current head through the
/// listing, finds the file this pipeline was imported from, and imports it again through the existing import
/// endpoint at that head commit; the backend re-checks the permissions, the policy and the commit. It then tells the
/// user, by a toast, which commit the pipeline follows now. Nothing is written when the file is unchanged, absent
/// from the mirror or refused: the toast says so.
/// </summary>
internal sealed class ExternalPipelineSync(ExternalPipelinesApi api, NotifyHelper toast)
{
    /// <summary>True when the pipeline was re-imported (its definition changed); false for every other outcome.</summary>
    public async Task<bool> SyncAsync(int projectId, int pipelineId, string pipelineName)
    {
        ExternalPipelineCandidatesDto candidates;
        try
        {
            candidates = await api.GetCandidatesAsync(projectId).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            toast.Notify(OmniSeverity.Danger, "ExternalPipelineSyncFailedTitle", exception.Message);
            return false;
        }

        var item = candidates.Items.FirstOrDefault(candidate =>
            candidate.PipelineId == pipelineId && candidate.State != ExternalPipelineImportState.NameTaken);
        if (item is null)
        {
            toast.Warning("ExternalPipelineSyncFailedTitle", "ExternalPipelineSyncMissingMessage", pipelineName);
            return false;
        }
        if (item.State == ExternalPipelineImportState.Imported)
        {
            toast.Info("ExternalPipelineSyncCurrentTitle", "ExternalPipelineSyncCurrentMessage",
                pipelineName, DisplayFormatting.ShortSha(item.CommitSha));
            return false;
        }
        if (item.State == ExternalPipelineImportState.Refused)
        {
            toast.Notify(OmniSeverity.Danger, "ExternalPipelineSyncFailedTitle", string.Join(" ", item.Problems));
            return false;
        }

        var outcome = await api.ImportAsync(projectId, new ImportExternalPipelineRequest
        {
            RepositoryId = item.RepositoryId,
            FilePath = item.FilePath,
            CommitSha = item.CommitSha
        }).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            toast.Notify(OmniSeverity.Danger, "ExternalPipelineSyncFailedTitle",
                outcome.ErrorMessage ?? outcome.Error?.Message ?? item.FilePath);
            return false;
        }
        toast.Success("ExternalPipelineSyncedTitle", "ExternalPipelineSyncedMessage",
            pipelineName, DisplayFormatting.ShortSha(outcome.Value!.ExternalImportCommit ?? item.CommitSha));
        return true;
    }
}
