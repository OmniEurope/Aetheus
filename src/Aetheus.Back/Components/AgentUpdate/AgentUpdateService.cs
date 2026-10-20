// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.AgentUpdate;

internal sealed class AgentUpdateService(
    // No IServerRepository: the two reads it supplied are own-reads on this module own repository
    // now, which is what took AgentUpdate out of the cycle with Servers.
    IHubContext<ServerHub> serverHub,
    ITaskService taskService,
    IAuditService audit,
    IAgentUpdateRepository updateRepo,
    IAgentReleaseCatalog releases,
    IAgentCompatibilityPolicy compatibilityPolicy,
    ILogger<AgentUpdateService> logger,
    AgentUpdateNotificationPublisher notifications) : IAgentUpdateService
{
    public async Task<AgentUpdateResponse> QueueUpdateAsync(
        int serverId,
        string requestedBy,
        CancellationToken ct = default)
    {
        // FindServerAsync also covers the existence check and gives us the name in one query,
        // which is needed for the TaskQueued broadcast payload.
        var server = await updateRepo.FindServerAsync(serverId, ct).ConfigureAwait(false);
        if (server is null)
            throw new NotFoundException($"Server {serverId} not found.");

        return await QueueForServerAsync(server, requestedBy, null, ct).ConfigureAwait(false);
    }

    public async Task<AgentUpdateResponse> QueueUpdateWithCapabilitiesAsync(
        int serverId,
        string requestedBy,
        IReadOnlyCollection<string> capabilities,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        // The whitelist is the whole point of this entry: anything else, an empty list included, is a
        // malformed request, and it is refused here before the server is even read.
        if (!AgentEnableableCapabilities.TryNormalize(capabilities, out var enable, out var rejected))
            throw new BadRequestException(
                $"Capability '{rejected}' cannot be enabled from an agent update. Allowed: "
                + string.Join(", ", AgentEnableableCapabilities.Whitelist) + ".");
        if (enable.Count == 0)
            throw new BadRequestException("At least one capability to enable is required.");

        var server = await updateRepo.FindServerAsync(serverId, ct).ConfigureAwait(false);
        if (server is null)
            throw new NotFoundException($"Server {serverId} not found.");
        // Only the Linux installer has the --enable-* flags; the Windows updater has nothing to map to.
        if (server.OsType != OsType.Linux)
            throw new BadRequestException("Capabilities can only be enabled on a Linux agent.");
        // An agent that predates the field would run the update and drop the request without a word: the
        // operator would be told the grant happened. Refuse by name instead and ask for a plain update.
        if (!AgentCapabilities.IsDeclared(server.AgentCapabilitiesJson, AgentCapabilities.EnableCapabilitiesOnUpdate))
            throw new ConflictException(
                "This agent cannot enable capabilities yet: update it first with the plain agent update.");

        return await QueueForServerAsync(server, requestedBy, enable, ct).ConfigureAwait(false);
    }

    private async Task<AgentUpdateResponse> QueueForServerAsync(
        Server server,
        string requestedBy,
        IReadOnlyList<string>? enable,
        CancellationToken ct)
    {
        var serverId = server.Id;

        // Deliberately no "already up to date, nothing to do" short-circuit. This method has exactly one
        // caller, the per-server Update agent action, which someone chose on one named server: they want
        // that agent reinstalled, and an agent reporting the target version is one of the reasons to want
        // it, not a reason to refuse. Skipping made the click do nothing on precisely the server the
        // operator was trying to repair. The fleet-wide sweep below still skips current agents, because
        // there nobody named the server.
        var (request, created) = enable is null
            ? await updateRepo.ReserveAsync(server, releases.Current, requestedBy, ct).ConfigureAwait(false)
            : await updateRepo.ReserveAsync(server, releases.Current, requestedBy, ct, enable).ConfigureAwait(false);
        // A reused request that already produced its task has a fixed environment: never answer
        // "queued" for a grant that update will not make.
        if (enable is not null && !CarriesAll(request, enable))
            throw new ConflictException(
                "An agent update is already running on this server without these capabilities. Retry once it has finished.");
        var (_, task, blockingCount) = await updateRepo.TryQueueAsync(request.Id, ct).ConfigureAwait(false);
        if (task is not null)
        {
            await BroadcastQueuedAsync(serverId, ct).ConfigureAwait(false);
            await taskService.NotifyTaskQueuedAsync(task, server.Name, ct).ConfigureAwait(false);
        }

        await audit.LogAsync(
            created ? "AgentUpdateReserved" : "AgentUpdateRequestReused",
            "Server",
            serverId,
            $"Agent update to {request.TargetVersion}; blocking tasks: {blockingCount}",
            ct).ConfigureAwait(false);
        if (enable is not null)
            await audit.LogAsync(
                "AgentCapabilityEnableRequested",
                "Server",
                serverId,
                $"Capabilities requested with the agent update to {request.TargetVersion}: "
                + AgentEnableableCapabilities.Join(enable),
                ct).ConfigureAwait(false);

        return ToResponse(request, task, blockingCount, !created);
    }

    private static bool CarriesAll(AgentUpdateRequest request, IReadOnlyList<string> requested)
    {
        AgentEnableableCapabilities.TryParse(request.EnableCapabilities, out var carried, out _);
        return requested.All(carried.Contains);
    }

    public async Task<AgentUpdateAllResponse> QueueUpdateAllAsync(
        List<int>? accessibleServerIds,
        string requestedBy,
        CancellationToken ct = default)
    {
        // accessibleServerIds == null means "no scoping restriction" (e.g. unrestricted admin).
        // An empty list means the caller can administer nothing → nothing to queue.
        if (accessibleServerIds is { Count: 0 })
            return new AgentUpdateAllResponse();

        var servers = await updateRepo.GetServersForAgentUpdateAsync(accessibleServerIds, ct).ConfigureAwait(false);
        if (servers.Count == 0)
            return new AgentUpdateAllResponse();

        var queuedIds = new List<int>(servers.Count);
        var results = new List<AgentUpdateResponse>(servers.Count);
        var offlineCount = 0;
        var alreadyUpToDateCount = 0;
        var busyCount = 0;
        foreach (var server in servers)
        {
            var id = server.Id;
            if (server.Status != ServerStatus.Online)
            {
                offlineCount++;
                results.Add(new AgentUpdateResponse
                {
                    ServerId = id,
                    SourceVersion = server.AgentVersion,
                    TargetVersion = releases.Current.SoftwareVersion,
                    Outcome = AgentUpdateQueueOutcome.Offline
                });
                continue;
            }
            if (compatibilityPolicy.Evaluate(ServerDataMapper.MapToDto(server)).Status
                == AgentCompatibilityStatus.UpToDate)
            {
                alreadyUpToDateCount++;
                results.Add(new AgentUpdateResponse
                {
                    ServerId = id,
                    SourceVersion = server.AgentVersion,
                    TargetVersion = releases.Current.SoftwareVersion,
                    Outcome = AgentUpdateQueueOutcome.AlreadyUpToDate
                });
                continue;
            }

            var (request, created) = await updateRepo
                .ReserveAsync(server, releases.Current, requestedBy, ct)
                .ConfigureAwait(false);
            var (_, task, blockingCount) = await updateRepo.TryQueueAsync(request.Id, ct).ConfigureAwait(false);
            if (blockingCount > 0) busyCount++;
            queuedIds.Add(id);
            results.Add(ToResponse(request, task, blockingCount, !created));
            if (task is not null)
            {
                await BroadcastQueuedAsync(id, ct).ConfigureAwait(false);
                await taskService.NotifyTaskQueuedAsync(task, server.Name, ct).ConfigureAwait(false);
            }
        }

        await audit.LogAsync("AgentSelfUpdateAll", "Server", null,
            $"Agent self-update queued for {queuedIds.Count} server(s)", ct).ConfigureAwait(false);

        return new AgentUpdateAllResponse
        {
            QueuedCount = queuedIds.Count,
            ServerIds = queuedIds,
            AlreadyUpToDateCount = alreadyUpToDateCount,
            OfflineCount = offlineCount,
            BusyCount = busyCount,
            Results = results
        };
    }

    public async Task<AgentUpdateAllPreviewDto> PreviewUpdateAllAsync(
        List<int>? accessibleServerIds,
        CancellationToken ct = default)
    {
        if (accessibleServerIds is { Count: 0 })
            return new AgentUpdateAllPreviewDto { TargetVersion = releases.Current.SoftwareVersion };

        var servers = await updateRepo
            .GetServersForCompatibilityAsync(accessibleServerIds, ct)
            .ConfigureAwait(false);
        var affected = 0;
        var alreadyCurrent = 0;
        var offline = 0;
        var incompatible = 0;
        var busy = 0;
        // One round trip for the whole fleet instead of one COUNT per outdated server: this renders a
        // preview dialog, and the per-server variant made that cost grow with the fleet.
        var activeTaskCounts = await updateRepo
            .CountActiveNonUpdateTasksAsync([.. servers.Select(server => server.Id)], ct)
            .ConfigureAwait(false);
        foreach (var server in servers)
        {
            if (server.Status != ServerStatus.Online)
            {
                offline++;
                continue;
            }

            var compatibility = compatibilityPolicy.Evaluate(server);
            if (compatibility.Status == AgentCompatibilityStatus.UpToDate)
            {
                alreadyCurrent++;
                continue;
            }

            affected++;
            if (compatibility.Status == AgentCompatibilityStatus.UpdateRequired)
                incompatible++;
            if (activeTaskCounts.TryGetValue(server.Id, out var activeTasks) && activeTasks > 0)
                busy++;
        }

        return new AgentUpdateAllPreviewDto
        {
            TargetVersion = releases.Current.SoftwareVersion,
            AffectedCount = affected,
            AlreadyUpToDateCount = alreadyCurrent,
            OfflineCount = offline,
            IncompatibleCount = incompatible,
            BusyCount = busy
        };
    }

    private static AgentUpdateResponse ToResponse(
        AgentUpdateRequest request,
        ServerTask? task,
        int blockingCount,
        bool existing) => new()
        {
            RequestId = request.Id,
            ServerId = request.ServerId,
            SourceVersion = request.ObservedVersion,
            TargetVersion = request.TargetVersion,
            TaskId = task?.Id ?? request.TaskId ?? 0,
            Status = request.Status,
            BlockingTaskCount = blockingCount,
            ExistingRequest = existing,
            Outcome = existing
                ? AgentUpdateQueueOutcome.ExistingRequest
                : blockingCount > 0
                    ? AgentUpdateQueueOutcome.WaitingForIdle
                    : AgentUpdateQueueOutcome.Queued
        };

    private async Task BroadcastQueuedAsync(int serverId, CancellationToken ct)
    {
        await Task.WhenAll(
            serverHub.Clients.Group(HubGroups.Server(serverId))
                .SendAsync("AgentUpdateQueued", serverId, ct),
            serverHub.Clients.Group(HubGroups.AllServers)
                .SendAsync("AgentUpdateQueued", serverId, ct)
        ).ConfigureAwait(false);
    }

    public async Task BroadcastProgressAsync(AgentUpdateProgressDto dto, CancellationToken ct = default)
    {
        // Defensive clamp: an agent reporting a corrupted percent must not poison the UI bar.
        // The progress is purely advisory - values outside [0,100] are coerced rather than
        // refused, so a buggy build still surfaces *something* useful (the phase enum).
        var clamped = dto with { Percent = Math.Clamp(dto.Percent, 0, 100) };
        var request = await updateRepo.MarkProgressAsync(dto.ServerId, clamped, ct).ConfigureAwait(false);
        if (clamped.Phase == AgentUpdatePhase.Failed && request is not null)
        {
            logger.LogError(
                "Agent update {SourceVersion} to {TargetVersion} failed on server {ServerId}: {Diagnostic}; correlation {CorrelationId}",
                request.ObservedVersion,
                request.TargetVersion,
                request.ServerId,
                request.FailureDiagnostic ?? "No diagnostic reported.",
                LogCorrelationIds.AgentUpdate(request.Id));
            await notifications.PublishOutcomeAsync(request, ct).ConfigureAwait(false);
        }
        await Task.WhenAll(
            serverHub.Clients.Group(HubGroups.Server(dto.ServerId))
                .SendAsync("AgentUpdateProgress", clamped, ct),
            serverHub.Clients.Group(HubGroups.AllServers)
                .SendAsync("AgentUpdateProgress", clamped, ct)
        ).ConfigureAwait(false);
    }
}
