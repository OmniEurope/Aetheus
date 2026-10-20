// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.AgentUpdate;

internal sealed class AgentUpdateCoordinatorService(
    IServiceScopeFactory scopeFactory,
    IHubContext<ServerHub> hub,
    TimeProvider timeProvider,
    ILogger<AgentUpdateCoordinatorService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>How long a task may stay unclaimed before an agent that cannot execute it is blamed.
    /// Long enough for a heartbeat to publish capabilities right after an update.</summary>
    internal static readonly TimeSpan UnexecutableTaskGrace = TimeSpan.FromMinutes(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync(
                "aetheus:agent-update-coordinator",
                RunLeaderLoopAsync,
                stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CoordinateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent update coordinator iteration failed");
            }
        }
    }

    internal async Task CoordinateAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentUpdateRepository>();
        var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var notifications = scope.ServiceProvider.GetRequiredService<AgentUpdateNotificationPublisher>();

        var waiting = await repository.GetWaitingRequestIdsAsync(ct).ConfigureAwait(false);
        foreach (var requestId in waiting)
        {
            var (request, task, _) = await repository.TryQueueAsync(requestId, ct).ConfigureAwait(false);
            if (task is null) continue;

            await taskService.NotifyTaskQueuedAsync(task, request.Server.Name, ct).ConfigureAwait(false);
            await hub.Clients.Groups([HubGroups.AllServers, HubGroups.Server(request.ServerId)])
                .SendAsync("AgentUpdateQueued", request.ServerId, ct)
                .ConfigureAwait(false);
            await audit.LogAsync(
                "AgentUpdateQueuedAfterDrain",
                "Server",
                request.ServerId,
                $"Agent update task {task.Id} queued after active work completed",
                ct).ConfigureAwait(false);
        }

        var expired = await repository.FailExpiredAsync(ct).ConfigureAwait(false);
        foreach (var request in expired)
        {
            logger.LogError(
                "Agent update {SourceVersion} to {TargetVersion} failed on server {ServerId}: {FailureCode} - {Diagnostic}; correlation {CorrelationId}",
                request.ObservedVersion,
                request.TargetVersion,
                request.ServerId,
                request.FailureCode,
                request.FailureDiagnostic,
                LogCorrelationIds.AgentUpdate(request.Id));
            await hub.Clients.Groups([HubGroups.AllServers, HubGroups.Server(request.ServerId)])
                .SendAsync("AgentUpdateFailed", request.ServerId, request.Id, ct)
                .ConfigureAwait(false);
            await audit.LogAsync(
                "AgentUpdateConfirmationFailed",
                "Server",
                request.ServerId,
                $"{request.FailureCode}: {request.FailureDiagnostic}",
                ct).ConfigureAwait(false);
            await notifications.PublishOutcomeAsync(request, ct).ConfigureAwait(false);
        }

        await RefuseUnexecutableTasksAsync(scope.ServiceProvider, repository, audit, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The task claim silently skips work an agent cannot execute, so a run whose step needs a
    /// capability the server's agent lacks sat Pending for as long as the agent kept polling. There is
    /// no policy that updates an agent by itself (an update is always an operator's action, from the
    /// server page), so waiting would only repeat the hang: such a task is failed, with the reason and
    /// the way out, once it has stayed unclaimed past the grace. Never while the server is mid-update
    /// (its fenced work resumes with the new agent) or while the agent is not polling (the offline
    /// handling owns that case).
    /// </summary>
    private async Task RefuseUnexecutableTasksAsync(
        IServiceProvider services,
        IAgentUpdateRepository repository,
        IAuditService audit,
        CancellationToken ct)
    {
        var release = services.GetRequiredService<IAgentReleaseCatalog>().Current;
        var refuser = services.GetRequiredService<IUnexecutableTaskRefuser>();

        var candidates = await repository
            .GetPendingTasksOfPollingAgentsAsync(UnexecutableTaskGrace, ct)
            .ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (AgentContractDiagnosis.Diagnose(candidate, release) is not { } gap)
                continue;
            if (!await refuser.RefuseAsync(candidate.TaskId, gap.FailureCode, gap.Reason, ct).ConfigureAwait(false))
                continue;

            logger.LogError(
                "Task {TaskId} ({Operation}) failed without running: {Reason}",
                candidate.TaskId,
                candidate.Operation,
                gap.Reason);
            await audit.LogAsync(
                "TaskRefusedAgentIncompatible",
                "ServerTask",
                candidate.TaskId,
                $"{candidate.Operation}: {gap.FailureCode}",
                ct).ConfigureAwait(false);
        }
    }
}
