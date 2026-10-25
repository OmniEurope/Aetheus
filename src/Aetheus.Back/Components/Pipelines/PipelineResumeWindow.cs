// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using static Aetheus.Back.Components.Pipelines.PipelineRunHelpers;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R-39: a deployment whose confirmation was missed waits for a resume instead of ending.
///
/// The confirmation window and its rollback are unchanged: the stage confirmation times out, the stage
/// fails, the <c>failed()</c> rollback puts the previous version back. What changes is the end. When
/// nothing is left but the workspace cleanup, the scheduler asks <see cref="TryParkAsync"/>, which parks
/// the run in <see cref="PipelineStatus.AwaitingResume"/> for the stage's window (24 hours by default),
/// before the cleanup: the workspace, the environment lock and every output stay for the resume.
///
/// Only that exact situation qualifies. A refused confirmation (a human said no), a cancellation, a
/// failure anywhere else, or a rollback that did not succeed entirely keep today's outcome, so a failed
/// rollback is never hidden behind a wait. It is on by default because it needs no definition change:
/// every pipeline with a stage confirmation and a deployment rollback (aetheus-deploy-prod,
/// aetheus-release-fast, a consumer of host-bluegreen-deploy) gets it; <c>resume_window_hours: 0</c>
/// turns it off for one stage.
/// </summary>
internal sealed class PipelineResumeWindow(
    IPipelineRepository repo,
    IHubContext<PipelineHub> pipelineHub,
    TimeProvider timeProvider,
    ILogger logger)
{
    /// <summary>The wait when the stage does not set <c>resume_window_hours</c>.</summary>
    internal const int DefaultHours = 24;

    /// <summary>The longest wait a definition may ask for: a week.</summary>
    internal const int MaxHours = 168;

    /// <summary>The hub message the run page reloads on when a run starts waiting.</summary>
    internal const string AwaitingResumeMessage = "PipelineRunAwaitingResume";

    /// <summary>Parks the run when its last stage confirmation timed out, its deployment was started and
    /// rolled back entirely, nothing else failed and only the workspace cleanup is left.</summary>
    /// <returns>True when the run now waits; the caller dispatches nothing more.</returns>
    public async Task<bool> TryParkAsync(
        PipelineRun run, PipelineYamlDefinition definition, IReadOnlyCollection<PipelineStepRun> pendingSteps,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(pendingSteps);
        if (pendingSteps.Any(step => step.StageName != SystemCleanupStage)) return false;
        if (HasCancellationRequest(run.AdditionalVariablesJson)) return false;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // A deadline already past: the window closed, either the expiry sweep re-opened the run to end it,
        // or a resumed run missed its confirmation again after the window it was first given.
        if (run.ResumeDeadline is { } deadline && deadline <= now) return false;

        var hours = await MissedConfirmationWindowAsync(run.Id, definition, ct).ConfigureAwait(false);
        if (hours is not > 0) return false;
        if (await repo.HasAnyRunningStepInRunAsync(run.Id, ct).ConfigureAwait(false)) return false;
        if (await repo.HasFailedArtifactCollectionAsync(run.Id, ct).ConfigureAwait(false)) return false;
        if (!await WasRolledBackAsync(run.Id, definition, ct).ConfigureAwait(false)) return false;

        // One window per run, counted from the first missed confirmation: a run that was resumed keeps the
        // deadline it was first given (still in the future here, a past one returned above), a run that
        // never waited gets a new one.
        var until = run.ResumeDeadline ?? now.AddHours(hours.Value);
        if (!await repo.TryParkRunAwaitingResumeAsync(run.Id, until, ct).ConfigureAwait(false)) return false;
        logger.LogWarning(
            "Run {RunId}: its confirmation was missed and the previous version is back; it waits for a resume until {Deadline:O}",
            run.Id, until);
        await pipelineHub.Clients.Groups(HubGroups.PipelineRunUpdates(run.Id, run.PipelineId))
            .SendAsync(AwaitingResumeMessage, run.Id, until, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The stages a resume replays: every stage that starts the deployment, and every stage that
    /// depends on one of them, directly or not. That is the new colour started again (the rollback
    /// stopped it), traffic switched, the checks, the confirmation, and what follows it.</summary>
    internal static List<string> StagesToReplay(PipelineYamlDefinition definition)
    {
        var stages = YamlParsingHelper.FlattenJobs(definition);
        var replay = stages.Where(PipelineDeploymentStages.IsStart)
            .Select(stage => stage.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (replay.Count == 0) return [];
        bool added;
        do
        {
            added = false;
            foreach (var stage in stages.Where(stage => !replay.Contains(stage.Name)
                         && stage.DependsOn.Any(replay.Contains)))
                added |= replay.Add(stage.Name);
        } while (added);
        return [.. stages.Where(stage => replay.Contains(stage.Name)).Select(stage => stage.Name)];
    }

    /// <summary>The window of the stage whose confirmation timed out last, or null when the last decided
    /// stage confirmation of the run did not time out (approved, refused, or none at all), or when the
    /// failed steps are not that stage's alone.</summary>
    private async Task<int?> MissedConfirmationWindowAsync(int runId, PipelineYamlDefinition definition, CancellationToken ct)
    {
        var last = (await repo.GetApprovalsAsync(runId, ct).ConfigureAwait(false) ?? [])
            .Where(approval => approval.Scope == ApprovalScope.Pipeline && approval.TimeoutMinutes is > 0
                && approval.Status != ApprovalStatus.Pending)
            .OrderByDescending(approval => approval.ResolvedAt ?? approval.RequestedAt)
            .ThenByDescending(approval => approval.Id)
            .FirstOrDefault();
        if (last?.Status != ApprovalStatus.TimedOut) return null;
        var failedStages = await repo.GetBlockingFailedStageNamesAsync(runId, ct).ConfigureAwait(false) ?? [];
        if (failedStages.Count != 1 || !string.Equals(failedStages[0], last.StageName, StringComparison.OrdinalIgnoreCase))
            return null;
        var stage = YamlParsingHelper.FlattenJobs(definition)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, last.StageName, StringComparison.OrdinalIgnoreCase));
        return stage is null ? null : stage.ResumeWindowHours ?? DefaultHours;
    }

    // The same test as the RolledBack status: the new colour was started and every rollback stage succeeded.
    private async Task<bool> WasRolledBackAsync(int runId, PipelineYamlDefinition definition, CancellationToken ct)
    {
        var rollback = PipelineDeploymentStages.RollbackNames(definition);
        var started = PipelineDeploymentStages.StartNames(definition);
        return rollback.Count > 0 && started.Count > 0
            && await repo.DidStagesAllSucceedAsync(runId, started, ct).ConfigureAwait(false)
            && await repo.DidStagesAllSucceedAsync(runId, rollback, ct).ConfigureAwait(false);
    }
}
