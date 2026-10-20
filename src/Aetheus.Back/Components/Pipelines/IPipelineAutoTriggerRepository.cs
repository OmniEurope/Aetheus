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

    Task<bool> HasActiveRunAsync(int pipelineId, CancellationToken ct = default);
    Task<bool> LockPipelineForWebhookAsync(int pipelineId, CancellationToken ct = default);
    Task<List<int>> GetActiveRunIdsAsync(int pipelineId, CancellationToken ct = default);

    Task<HashSet<int>> GetPipelineIdsWithActiveRunsAsync(CancellationToken ct = default);
}
