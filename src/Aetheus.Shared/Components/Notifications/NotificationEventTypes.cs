// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Notifications;

/// <summary>
/// Every event type the backend raises through the notification service, with the per-user default
/// that applies while the user has saved no preference for it. The list is read from the emitting
/// code, not invented: a type is here because something calls <c>SendEventAsync</c> with it.
/// <see cref="NotificationEventTypeDescriptor.CarriesProjectId"/> says whether its payload names a
/// project; only those can reach a user, because the recipients of an event are the subscribers of
/// its project. A type whose payload carries no project is still listed so the choice is visible, but
/// it produces no per-user notification.
/// </summary>
public static class NotificationEventTypes
{
    public const string PipelineFailed = "pipeline.failed";
    public const string PipelineSucceeded = "pipeline.succeeded";
    /// <summary>Recette R1-77: a run an assurance seal rejected, as a final result of its own beside failed and succeeded.</summary>
    public const string PipelineRejected = "pipeline.rejected";
    public const string PipelineApprovalRequested = "pipeline.approval-requested";
    /// <summary>An automated trigger (schedule, push, upstream run) whose launch the configuration
    /// checks refused. Nobody is there to read the refusal: it is recorded as a failed run carrying
    /// the reason (PipelineRefusedLaunchRecorder), and this notification tells the
    /// subscribers.</summary>
    public const string PipelineLaunchRefused = "pipeline.launch-refused";
    /// <summary>A release recorded as deployed (a deploy step past its health gate, or a <c>type:
    /// release</c> step with <c>deployed: true</c>), for the subscribers of its project.</summary>
    public const string ReleaseDeployed = "release.deployed";
    /// <summary>An agent update the agent's heartbeat confirmed, for the subscribers of every
    /// project the server is attached to. A server attached to no project notifies
    /// nobody.</summary>
    public const string AgentUpdateCompleted = "agent-update.completed";
    /// <summary>An agent update that failed (failure reported by the agent, wrong version, deadline
    /// passed), for the subscribers of every project the server is attached to.</summary>
    public const string AgentUpdateFailed = "agent-update.failed";
    /// <summary>Recette R3-16: a run of an AI task finished, with a link to it on the task's results page,
    /// for the subscribers of its project (or of the projects its server is attached to).</summary>
    public const string AiTaskCompleted = "ai-task.completed";
    /// <summary>Recette R1-77, a switch rather than an event (nothing is raised with it): a subscriber who enables it
    /// is also e-mailed the result of the pipelines started by another pipeline; off by default, the e-mail goes
    /// only for the root run. Their in-app notifications are unchanged.</summary>
    public const string PipelineChildResults = "pipeline.child-results";

    public static IReadOnlyList<NotificationEventTypeDescriptor> All { get; } =
    [
        new(PipelineFailed, DefaultEnabled: true, CarriesProjectId: true),
        new(PipelineSucceeded, DefaultEnabled: false, CarriesProjectId: true),
        new(PipelineRejected, DefaultEnabled: true, CarriesProjectId: true),
        new(PipelineApprovalRequested, DefaultEnabled: true, CarriesProjectId: true),
        new(PipelineLaunchRefused, DefaultEnabled: true, CarriesProjectId: true),
        new(ReleaseDeployed, DefaultEnabled: true, CarriesProjectId: true),
        new(AgentUpdateCompleted, DefaultEnabled: false, CarriesProjectId: true),
        new(AgentUpdateFailed, DefaultEnabled: true, CarriesProjectId: true),
        // Recette R3-16: in the inbox only for now, the owner wants no e-mail for it yet.
        new(AiTaskCompleted, DefaultEnabled: true, CarriesProjectId: true, SentByEmail: false),
        new(PipelineChildResults, DefaultEnabled: false, CarriesProjectId: true, SentByEmail: false),
        new("analysis.report.completed", DefaultEnabled: false, CarriesProjectId: true),
        new("analysis.gate.warning", DefaultEnabled: false, CarriesProjectId: true),
        new("analysis.gate.blocked", DefaultEnabled: false, CarriesProjectId: true),
        new("analysis.gate.error", DefaultEnabled: false, CarriesProjectId: true),
        new("analysis.cve-sync.failed", DefaultEnabled: false, CarriesProjectId: true),
        new("analysis.governance.expiring", DefaultEnabled: false, CarriesProjectId: true),
        new("analysis.operational.cve-sync-failed", DefaultEnabled: false, CarriesProjectId: true),
        // Raised per project (stale sync) and per server (stale Trivy database); only the first reaches users.
        new("analysis.operational.cve-db-stale", DefaultEnabled: false, CarriesProjectId: true),
        new("analysis.operational.storage-drift", DefaultEnabled: false, CarriesProjectId: true),
        new("analysis.operational.scanner-obsolete", DefaultEnabled: false, CarriesProjectId: false),
        new("analysis.operational.workspace-residual", DefaultEnabled: false, CarriesProjectId: false),
        new("analysis.operational.scanner-unavailable", DefaultEnabled: false, CarriesProjectId: false),
        new("app.down", DefaultEnabled: false, CarriesProjectId: true),
        new("app.recovered", DefaultEnabled: false, CarriesProjectId: true),
        new("app.analytics.quota", DefaultEnabled: false, CarriesProjectId: true),
        new("project.storage.quota", DefaultEnabled: false, CarriesProjectId: true),
        new("app.metric.threshold", DefaultEnabled: false, CarriesProjectId: true),
        new("alert.triggered", DefaultEnabled: false, CarriesProjectId: false),
        new("certbot.renewal-check.failed", DefaultEnabled: false, CarriesProjectId: false),
        // Carries the project owning the vault; a vault of a shared environment has none.
        new("SecretExpiring", DefaultEnabled: false, CarriesProjectId: true)
    ];

    /// <summary>The catalogue entry for <paramref name="eventType"/>, or null for an unknown type.</summary>
    public static NotificationEventTypeDescriptor? Find(string eventType) =>
        All.FirstOrDefault(descriptor => string.Equals(descriptor.EventType, eventType, StringComparison.Ordinal));
}
