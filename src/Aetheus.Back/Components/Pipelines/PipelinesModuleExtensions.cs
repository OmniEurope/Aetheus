// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Seeding;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines;

public static class PipelinesModuleExtensions
{
    public static IServiceCollection AddPipelinesModule(this IServiceCollection services)
    {
        services.AddScoped<IPipelineRepository, PipelineRepository>();
        services.AddScoped<IPipelineRunLineageReader, PipelineRunLineageRepository>();
        services.AddScoped<IPipelineRunLookupReader, PipelineRunLookupRepository>();
        services.AddScoped<IPipelineTemplateRepository, PipelineTemplateRepository>();
        services.AddScoped<IPipelineRunnerTargetReader, PipelineServerResolver>();
        services.AddScoped<IPipelineAutoTriggerRepository, PipelineAutoTriggerRepository>();
        services.AddScoped<IPipelineApprovalRepository, PipelineApprovalRepository>();
        services.AddScoped<IPipelineQualityResultRepository, PipelineCoverageRepository>();
        services.AddScoped<IPipelineArtifactRepository, PipelineArtifactRepository>();
        services.AddScoped<IPipelineEnvironmentReader, PipelineEnvironmentRepository>();
        services.AddScoped<IPipelineReleaseNumberRepository, ProjectReleaseNumberRepository>();
        services.AddScoped<IProjectReleaseSeriesRepository, ProjectReleaseNumberRepository>();
        services.AddScoped<IProjectReleaseSeriesService, ProjectReleaseSeriesService>();
        services.AddScoped<IPipelineRunRetentionRepository, PipelineRunRetentionRepository>();
        services.AddScoped<IPipelineProjectOwnerReader, PipelineProjectOwnerRepository>();
        services.AddScoped<IPipelineResourceLockRepository, PipelineResourceLockRepository>();
        services.AddScoped<IPipelineResumeWaitRepository, PipelineResumeWaitRepository>();
        services.AddScoped<IPipelineRunLineageService, PipelineRunLineageService>();
        services.AddScoped<IPipelineFavoriteRepository, PipelineFavoriteRepository>();
        services.AddScoped<IPipelineFavoriteService, PipelineFavoriteService>();
        services.AddScoped<IPipelineGitService, PipelineGitService>();
        services.AddScoped<IProjectConfigRepositoryService, ProjectConfigRepositoryService>();
        services.AddScoped<IPipelineEnvFilePreparer, PipelineEnvFilePreparer>();
        services.AddScoped<IPipelineProductionSetupService, PipelineProductionSetupService>();
        services.AddScoped<IPipelineVariableResolver, PipelineVariableResolver>();
        services.AddScoped<IPipelineService, PipelineService>();
        services.AddHostedService<TaskTimeoutService>();
        services.AddScoped<IPipelineRunService, PipelineRunService>();
        services.AddScoped<IPipelineLauncher>(sp => (IPipelineLauncher)sp.GetRequiredService<IPipelineRunService>());
        services.AddScoped<IPipelineApprovalService, PipelineApprovalService>();
        services.AddScoped<IPipelineArtifactService, PipelineArtifactService>();
        services.AddScoped<IPipelineFleetRepository, PipelineFleetRepository>();
        services.AddScoped<IPipelineFleetService, PipelineFleetService>();
        services.AddScoped<IPipelineTemplateResolver, PipelineTemplateResolver>();
        services.AddScoped<IPipelineEnvironmentCheckGuard, PipelineEnvironmentCheckGuard>();
        services.AddScoped<IPipelineDispatchServerResolver, PipelineDispatchServerResolver>();
        services.AddScoped<IPipelineStepTaskBuilder, PipelineStepTaskBuilder>();
        services.AddScoped<IPipelineAnalysisTaskFactory, PipelineAnalysisTaskFactory>();
        services.AddScoped<IPipelineDotnetTestTaskFactory, PipelineDotnetTestTaskFactory>();
        services.AddScoped<IPipelineFunctionTaskFactory, PipelineFunctionTaskFactory>();
        services.AddScoped<IPipelineGateStatusTaskFactory, PipelineGateStatusTaskFactory>();
        services.AddScoped<IPipelineDeploymentTaskFactory, PipelineDeploymentTaskFactory>();
        services.AddScoped<IPipelineHostOperationTaskFactory, PipelineHostOperationTaskFactory>();
        services.AddScoped<IPipelineArtifactTaskFactory, PipelineArtifactTaskFactory>();
        services.AddScoped<IPipelineScannerTaskFactory, PipelineScannerTaskFactory>();
        services.AddScoped<IPipelineRunFinalizer, PipelineRunFinalizer>();
        services.AddScoped<IPipelineRunParameterResolver, PipelineRunParameterResolver>();
        services.AddScoped<IPipelineCheckpointReuseService, PipelineCheckpointReuseService>();
        services.AddScoped<IPipelineWorkspaceSourceResolver, PipelineWorkspaceSourceResolver>();
        services.AddScoped<IPipelineRunPreparationService, PipelineRunPreparationService>();
        services.AddScoped<IPipelineRequirementsChecker, PipelineRequirementsChecker>();
        services.AddScoped<IPipelineChildPipelineResolver, PipelineChildPipelineResolver>();
        services.AddScoped<IPipelineAdvisoryPreflightBuilder, PipelineAdvisoryPreflightBuilder>();
        services.AddScoped<IPipelineScannerManifestPreflight, PipelineScannerManifestPreflight>();
        services.AddScoped<IPipelineReleaseArtifactPreflight, PipelineReleaseArtifactPreflight>();
        services.AddScoped<IPipelineAiProfilePreflight, PipelineAiProfilePreflight>();
        services.AddScoped<IPipelineConfigTemplatePreflight, PipelineConfigTemplatePreflight>();
        services.AddScoped<IPipelineRunPreflightService, PipelineRunPreflightService>();
        services.AddScoped<IPipelineSetupReadinessService, PipelineSetupReadinessService>();
        services.AddScoped<IPipelineRequirementsProvisioner, PipelineRequirementsProvisioner>();
        services.AddScoped<IPipelineResourceUsageRepository, PipelineResourceUsageRepository>();
        services.AddScoped<IPipelineResourceUsageService, PipelineResourceUsageService>();
        services.AddScoped<IPipelineProjectCopyService, PipelineProjectCopyService>();
        services.AddScoped<IRunStageBaselineService, RunStageBaselineService>();
        services.AddScoped<IPipelineMigrationGateRepository, PipelineMigrationGateRepository>();
        services.AddScoped<IPipelineMigrationGateService, PipelineMigrationGateService>();
        services.AddScoped<IPipelineSchemaTrackingService, PipelineSchemaTrackingService>();
        services.AddScoped<IPipelinePortRegistryGuard, PipelinePortRegistryGuard>();
        services.AddScoped<IPipelineRunDefinitionParser, PipelineRunDefinitionParser>();
        services.AddScoped<IPipelineRunControlService, PipelineRunControlService>();
        services.AddScoped<IPipelineSystemTaskFactory, PipelineSystemTaskFactory>();
        services.AddScoped<IPipelineTriggerStepCoordinator, PipelineTriggerStepCoordinator>();
        services.AddScoped<IPipelineBranchAdvanceStep, PipelineBranchAdvanceStep>();
        services.AddScoped<IPipelineStepTaskDispatcher, PipelineStepTaskDispatcher>();
        services.AddScoped<IPipelineRunReader, PipelineRunReader>();
        services.AddScoped<IPipelineStageDispatchPlanner, PipelineStageDispatchPlanner>();
        services.AddScoped<IPipelineRunScheduler, PipelineRunScheduler>();
        // Launcher -> scheduler is an injected edge; scheduler -> launcher travels as a method
        // parameter (IPipelineChildRunLauncher), so the recursion closes without a DI cycle.
        services.AddScoped<IPipelineRunLauncher, PipelineRunLauncher>();
        services.AddScoped<IPipelineTemplateService, PipelineTemplateService>();
        services.AddScoped<IPipelineWebhookService, PipelineWebhookService>();
        services.AddScoped<IPipelineRunToEndPolicy, PipelineRunToEndPolicy>();
        services.AddScoped<IPipelineOwnerAuthorization, PipelineOwnerAuthorization>();
        // Administrators change pipeline owners; Users asks for it before deactivating an owner.
        services.AddScoped<IPipelineOwnershipRepository, PipelineOwnershipRepository>();
        services.AddScoped<IPipelineOwnershipService, PipelineOwnershipService>();
        services.AddScoped<
            IDomainEventHandler<Users.Events.UserPipelinesTransferRequestedEvent>,
            UserPipelinesTransferHandler>();
        // Only the colour behind the public API address leads (decision of 2026-10-02): the lease asks
        // the probe, which asks the address itself which instance answers.
        services.AddHttpClient(LiveInstanceProbe.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<LiveInstanceProbe>();
        services.AddSingleton<ILiveInstanceProbe>(provider => provider.GetRequiredService<LiveInstanceProbe>());
        services.AddHostedService(provider => provider.GetRequiredService<LiveInstanceProbe>());
        services.AddSingleton<IPostgresLeaderLease, PostgresLeaderLease>();
        // Pipeline chaining: trigger downstream pipelines when an upstream run succeeds (on_success:).
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Events.PipelineRunCompletedEvent>, PipelineRunCompletedDownstreamHandler>();
        // Orchestration: complete a waiting `type: trigger` step when its child run finishes.
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Events.PipelineRunCompletedEvent>, PipelineRunCompletedTriggerHandler>();
        // A failed parent run cancels the runs it triggered that are still in flight (R-31).
        services.AddScoped<PipelineFailedParentChildCanceller>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Events.PipelineRunCompletedEvent>, PipelineRunCompletedChildCancellationHandler>();
        // Notifications: a failed or succeeded run, and an approval gate, become notification events.
        services.AddScoped<PipelineRunNotificationPublisher>();
        services.AddScoped<PipelineRefusedLaunchRecorder>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Events.PipelineRunCompletedEvent>, PipelineRunCompletedNotificationHandler>();
        services.AddScoped<Services.DomainEvents.IDomainEventHandler<Events.PipelineApprovalRequestedEvent>, PipelineApprovalRequestedNotificationHandler>();
        // Git announces a push; this module decides what it means for pipelines. Observer dispatch:
        // a push already written to disk must not be reported as failed because a trigger did not fire.
        services.AddScoped<PipelineDefinitionSynchronizer>();
        services.AddScoped<
            Services.DomainEvents.IDomainEventHandler<Git.Events.GitPushProcessedEvent>,
            GitPushPipelineTriggerHandler>();
        // A branch the server moved itself (post-deployment advance, merged pull request) follows the
        // same definition sync as a push, and triggers nothing (ADR-044).
        services.AddScoped<
            Services.DomainEvents.IDomainEventHandler<Git.Events.GitBranchMovedEvent>,
            GitBranchMovedPipelineSyncHandler>();
        // Orchestration, inverted: Tasks announces that a pipeline-owned task settled, and this side
        // decides what the run does next. Both are dispatched strictly, so a failure here reaches the
        // Tasks caller exactly as the direct call it replaced did.
        services.AddScoped<
            Services.DomainEvents.IDomainEventHandler<Tasks.Events.PipelineStepTaskCompletedEvent>,
            PipelineStepTaskCompletedHandler>();
        services.AddScoped<
            Services.DomainEvents.IDomainEventHandler<Tasks.Events.PipelineStepTaskMetricsCollectedEvent>,
            PipelineStepTaskMetricsHandler>();
        services.AddScoped<
            Services.DomainEvents.IDomainEventHandler<Tasks.Events.PipelineStepMigrationGateReportedEvent>,
            PipelineStepMigrationGateHandler>();
        // SSRF hardening: the string-level IsSafeOutboundUrl guard in PipelineEnvironmentCheckGuard can be
        // bypassed by DNS-rebinding (TOCTOU between the check and the request). Re-validate the
        // resolved IP at connect time via SocketsHttpHandler.ConnectCallback, mirroring the
        // "webhooks" client, so a short-TTL host that resolves to a forbidden address is rejected.
        services.AddHttpClient(PipelineEnvironmentCheckGuard.EnvironmentCheckHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectCallback = async (ctx, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    var safe = addresses.FirstOrDefault(a => !WebhookSsrfGuard.IsForbiddenAddress(a))
                        ?? throw new HttpRequestException("Environment check target resolved to a forbidden address.");

                    var socket = new Socket(safe.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(safe, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            });
        services.AddHostedService<PipelineSchedulerService>();
        services.AddHostedService<PipelineRetentionService>();
        services.AddHostedService<PipelineTriggerReconcileService>();
        // An environment linked to a project gets its pipelines copied into the project's repository.
        services.AddScoped<IDomainEventHandler<Environments.EnvironmentLinkedToProjectEvent>, EnvironmentPipelinesCopyHandler>();

        // Generic audit observers of this module's events (moved from the Shared module, 2026-09-25).
        services.AddScoped<IDomainEventHandler<Events.PipelineRunCompletedEvent>, DomainEventAuditHandler<Events.PipelineRunCompletedEvent>>();
        services.AddScoped<IDomainEventHandler<Events.PipelineApprovalRequestedEvent>, DomainEventAuditHandler<Events.PipelineApprovalRequestedEvent>>();
        return services;
    }
}
