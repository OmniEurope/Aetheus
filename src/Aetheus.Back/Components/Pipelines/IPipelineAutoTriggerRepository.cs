// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Automatic pipeline triggers: the pipelines a webhook, a Git push or the cron schedule may start,
/// the per-pipeline lock a webhook takes, and the active-run checks that decide whether a trigger
/// queues, skips or supersedes. The port of the webhook service, the Git push trigger handler and the
/// schedule service (audit AUD-07). <see cref="IPipelineRepository"/> composes it and delegates to the
/// same queries.
/// </summary>
public interface IPipelineAutoTriggerRepository
{
    Task<List<Pipeline>> GetWebhookTriggeredPipelinesWithProjectAsync(CancellationToken ct = default);
    Task<List<Pipeline>> GetWebhookTriggeredPipelinesForProjectAsync(int projectId, CancellationToken ct = default);

    Task<List<Pipeline>> GetScheduledPipelinesAsync(CancellationToken ct = default);

    /// <summary>One aggregate query over the scheduled pipelines, to know whether
    /// <see cref="GetScheduledPipelinesAsync"/> would return something different from last time.</summary>
    Task<ScheduledPipelinesFingerprint> GetScheduledPipelinesFingerprintAsync(CancellationToken ct = default);

    /// <summary>Moves the pipeline's <see cref="Pipeline.LastScheduledOccurrence"/> forward to
    /// <paramref name="to"/>, only when it is null or earlier. One conditional update, so of several
    /// instances racing for the same occurrence exactly one gets <c>true</c>; <c>false</c> means another
    /// instance took it, or the pipeline no longer exists. It does not touch <c>UpdatedAt</c>.</summary>
    Task<bool> TryAdvanceScheduleCursorAsync(int pipelineId, DateTime to, CancellationToken ct = default);

    /// <summary>Puts the cursor back to <paramref name="previous"/> when it still holds
    /// <paramref name="claimed"/>: the occurrence was taken but its start failed for a reason that says
    /// nothing about the occurrence itself, so the next tick may take it again.</summary>
    Task RestoreScheduleCursorAsync(int pipelineId, DateTime claimed, DateTime? previous, CancellationToken ct = default);

    Task<bool> HasActiveRunAsync(int pipelineId, CancellationToken ct = default);
    Task<bool> LockPipelineForWebhookAsync(int pipelineId, CancellationToken ct = default);
    Task<List<int>> GetActiveRunIdsAsync(int pipelineId, CancellationToken ct = default);

    Task<HashSet<int>> GetPipelineIdsWithActiveRunsAsync(CancellationToken ct = default);
}
