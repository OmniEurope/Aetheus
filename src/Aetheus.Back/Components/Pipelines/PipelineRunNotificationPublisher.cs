// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Records project outcomes through <see
/// cref="IUserNotificationService.RecordProjectEventAsync"/> for individual subscribers, separately
/// from administrative channel rules and AI triggers. Runs without a resolvable project produce no
/// delivery.</summary>
public sealed class PipelineRunNotificationPublisher(
    IPipelineRepository repo,
    IUserNotificationService userNotifications)
{
    /// <summary>The event type a terminal status is recorded as, or null for a status that is not recorded.</summary>
    public static string? EventTypeFor(PipelineStatus status) => status switch
    {
        PipelineStatus.Failed => NotificationEventTypes.PipelineFailed,
        PipelineStatus.Success => NotificationEventTypes.PipelineSucceeded,
        PipelineStatus.Rejected => NotificationEventTypes.PipelineRejected,
        _ => null
    };

    public async Task PublishRunCompletedAsync(int pipelineRunId, PipelineStatus status, CancellationToken ct = default)
    {
        if (EventTypeFor(status) is not { } eventType) return;
        // Recette R1-77: the e-mail of a run another pipeline started (a trigger step: the parent waits for
        // it, so the parent's own result covers it) goes only to the subscribers who asked for it, with a
        // link to the run at the root of its lineage. A run started alone is its own root and mails as
        // before; so does an on_success follow-up, which starts once its upstream run has finished and
        // mailed its own result, so no other mail covers it.
        var root = await RootRunAsync(pipelineRunId, ct).ConfigureAwait(false);
        var isChild = root is not null && root.RunId != pipelineRunId;
        await RecordRunEventAsync(pipelineRunId, eventType, (run, projectId) => new
        {
            // First property: EmailOnceMarker matches the serialized payload on it.
            PipelineRunId = run.Id,
            run.PipelineId,
            PipelineName = run.Pipeline!.Name,
            run.BuildNumber,
            ProjectId = projectId,
            Status = status.ToString(),
            IsChildRun = isChild,
            RootRunId = root?.RunId ?? pipelineRunId,
            RootPipelineName = isChild ? root!.PipelineName : null
        }, ct, isChild ? NotificationEventTypes.PipelineChildResults : null, EmailOnceMarker(pipelineRunId)).ConfigureAwait(false);
    }

    /// <summary>Recette R1-77: the payload fragment of one run's result. A retried run that ends with the
    /// same result again (same event type) is not e-mailed a second time to a recipient already mailed.</summary>
    internal static string EmailOnceMarker(int pipelineRunId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"PipelineRunId\":{pipelineRunId},");

    /// <summary>The run that started the trigger lineage of <paramref name="runId"/>; null when its lineage
    /// cannot be read (the run is then treated as its own root).</summary>
    private async Task<PipelineRunRootReference?> RootRunAsync(int runId, CancellationToken ct)
    {
        var roots = await repo.GetRootRunReferencesAsync([runId], ct).ConfigureAwait(false);
        return roots is not null && roots.TryGetValue(runId, out var root) ? root : null;
    }

    public async Task PublishApprovalRequestedAsync(PipelineApprovalRequestedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await RecordRunEventAsync(domainEvent.PipelineRunId, NotificationEventTypes.PipelineApprovalRequested, (run, projectId) => new
        {
            PipelineRunId = run.Id,
            run.PipelineId,
            PipelineName = run.Pipeline!.Name,
            run.BuildNumber,
            ProjectId = projectId,
            domainEvent.StageName,
            domainEvent.EnvironmentName
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Notifies project subscribers when configuration checks refuse an automated launch,
    /// including its trigger and refusal reasons.</summary>
    public async Task PublishLaunchRefusedAsync(
        Pipeline pipeline, string triggerSource, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var projectId = await repo.GetPipelineProjectIdAsync(pipeline, ct).ConfigureAwait(false);
        await userNotifications.RecordProjectEventAsync(
            NotificationEventTypes.PipelineLaunchRefused,
            JsonSerializer.Serialize(new
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                ProjectId = projectId,
                TriggerSource = triggerSource,
                Reason = reason
            }),
            ct).ConfigureAwait(false);
    }

    // Shared by both events: a run whose pipeline cannot be loaded records nothing.
    private async Task RecordRunEventAsync(
        int pipelineRunId, string eventType, Func<PipelineRun, int?, object> buildPayload, CancellationToken ct,
        string? emailOptInEventType = null, string? emailOnceMarker = null)
    {
        var run = await repo.GetPipelineRunWithPipelineAsync(pipelineRunId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return;
        var projectId = await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        await userNotifications.RecordProjectEventAsync(
                eventType, JsonSerializer.Serialize(buildPayload(run, projectId)), ct, emailOptInEventType, emailOnceMarker)
            .ConfigureAwait(false);
    }
}
