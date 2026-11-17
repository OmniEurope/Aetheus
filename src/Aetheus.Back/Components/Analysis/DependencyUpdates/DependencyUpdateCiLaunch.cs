// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>
/// PLAN-007: the CI of the project on an update's branch, at the commit the branch holds once it is complete (the bump, and the
/// refreshed lock files when the bump needed them). Its QA follows when the CI succeeds (<see cref="DependencyUpdateQaFollowUp"/>).
/// A missing pipeline or a refused launch is said on the row, never hidden: the branch stays and the user sees what was not run.
/// </summary>
internal sealed class DependencyUpdateCiLaunch(DependencyUpdateRepository repository, IDependencyUpdatePipelines pipelines, TimeProvider timeProvider)
{
    private const int MaxReasonLength = 2048;

    public async Task LaunchAsync(DependencyUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        var available = await repository.GetPipelinesAsync(update.ProjectId, update.GitInternalRepoId, ct).ConfigureAwait(false);
        var notes = new List<string>();
        if (DependencyUpdateRoles.Ci(available) is { } ci)
        {
            update.CiPipelineId = ci.Id;
            var launch = await pipelines.LaunchAsync(ci.Id, update.Branch!, update.CommitSha!, update.Id, ct).ConfigureAwait(false);
            update.CiPipelineRunId = launch.RunId;
            if (launch.Refusal is { } refusal) notes.Add($"CI '{ci.Name}' not started: {refusal}");
        }
        else
        {
            notes.Add("The project has no CI pipeline (template application-ci) to run on the branch.");
        }

        // The QA is only found here; DependencyUpdateQaFollowUp launches it once the CI run succeeds, because it restores the CI's
        // artifacts of the same commit. A candidate is never launched: it would allocate and publish a release.
        if (DependencyUpdateRoles.Qa(available) is { } qa) update.QaPipelineId = qa.Id;
        else notes.Add("The project has no QA pipeline (template application-qa) to run on the branch after the CI.");
        if (update.QaPipelineId is not null && update.CiPipelineRunId is null)
            notes.Add("QA not started: it restores the artifacts of a CI run of this commit, and there is none.");

        var text = notes.Count == 0 ? null : string.Join(" ", notes);
        update.Reason = text is null || text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
        update.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repository.SaveAsync(ct).ConfigureAwait(false);
    }
}
