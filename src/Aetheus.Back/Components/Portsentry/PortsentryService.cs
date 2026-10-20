// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Portsentry;

public class PortsentryService(IPortsentryRepository repo, IAuditService audit, ITaskService taskService) : IPortsentryService
{
    private Task QueueTaskAsync(ServerTask task, CancellationToken ct = default)
        => TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct);

    public async Task<PortsentryDataDto> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null)
            return new PortsentryDataDto();

        return new PortsentryDataDto
        {
            IsInstalled = true,
            IsRunning = state.IsRunning,
            Version = state.Version,
            Mode = state.Mode,
            TcpPorts = state.TcpPorts,
            UdpPorts = state.UdpPorts,
            BlockedCount = state.BlockedCount
        };
    }

    public async Task ExecuteActionAsync(int serverId, PortsentryActionRequest request, CancellationToken ct = default)
    {
        var kind = request.Action switch
        {
            PortsentryAction.Start => OperationKind.PortsentryStart,
            PortsentryAction.Stop => OperationKind.PortsentryStop,
            PortsentryAction.Restart => OperationKind.PortsentryRestart,
            _ => throw new BadRequestException($"Unknown portsentry action: {request.Action}")
        };

        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"PortSentry - {request.Action}", kind, target: "-", timeoutSeconds: 30), ct).ConfigureAwait(false);

        await audit.LogAsync($"Portsentry{request.Action}", "Portsentry", serverId, request.Action.ToString(), ct).ConfigureAwait(false);
    }

    public async Task SetupAsync(int serverId, PortsentrySetupRequest request, CancellationToken ct = default)
    {
        // Typed op: the scan mode is the task target; the TCP/UDP port lists ride in env vars. The agent
        // re-validates every field and dispatches the root-owned portsentry-setup helper (argv-exact
        // sudoers grant) - replacing the old sed/apt-get shell chain the agent's CommandValidator rejected.
        var env = new Dictionary<string, string>
        {
            [PortsentrySetupEnv.TcpPorts] = request.TcpPorts,
            [PortsentrySetupEnv.UdpPorts] = request.UdpPorts
        };

        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, "PortSentry - setup", OperationKind.PortsentrySetup, target: request.Mode, environmentVariables: env, timeoutSeconds: 120), ct).ConfigureAwait(false);

        await audit.LogAsync("PortsentrySetup", "Portsentry", serverId, $"Mode={request.Mode}", ct).ConfigureAwait(false);
    }

    public async Task GetLogsAsync(int serverId, PortsentryLogRequest request, CancellationToken ct = default)
    {
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, "PortSentry - logs", OperationKind.PortsentryGetLogs, target: "-", timeoutSeconds: 15), ct).ConfigureAwait(false);
    }

    public async Task UnblockIpAsync(int serverId, PortsentryUnblockRequest request, CancellationToken ct = default)
    {
        if (!PortsentryCommandHelper.IsValidIpAddress(request.IpAddress))
            throw new BadRequestException("Invalid IP address.");

        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"PortSentry - unblock {request.IpAddress}", OperationKind.PortsentryUnblock, target: request.IpAddress, timeoutSeconds: 15), ct).ConfigureAwait(false);

        await audit.LogAsync("PortsentryUnblock", "Portsentry", serverId, request.IpAddress, ct).ConfigureAwait(false);
    }

    public async Task GetStatusAsync(int serverId, CancellationToken ct = default)
    {
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, "PortSentry - status", OperationKind.PortsentryStatus, target: "-", timeoutSeconds: 15), ct).ConfigureAwait(false);
    }

    public async Task<List<PortsentryWhitelistIpDto>> GetWhitelistAsync(int serverId, CancellationToken ct = default)
    {
        var list = await repo.GetWhitelistAsync(serverId, ct).ConfigureAwait(false);
        return list.Select(w => new PortsentryWhitelistIpDto
        {
            Id = w.Id,
            IpAddress = w.IpAddress,
            Description = w.Description,
            CreatedAt = w.CreatedAt
        }).ToList();
    }

    public async Task<PaginatedResult<PortsentryBlockedIpDto>> GetBlockedIpsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetBlockedIpsPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<PortsentryBlockedIpDto>
        {
            Items = items.Select(MapBlockedIp).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PaginatedResult<PortsentryWhitelistIpDto>> GetWhitelistAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetWhitelistPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<PortsentryWhitelistIpDto>
        {
            Items = items.Select(MapWhitelistIp).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>What the blocked IPs grid's checkable Protocol filter offers.</summary>
    public async Task<PortsentryFilterValuesDto> GetFilterValuesAsync(int serverId, CancellationToken ct = default) =>
        new() { Protocols = await repo.GetBlockedProtocolsAsync(serverId, ct).ConfigureAwait(false) };

    public async Task<PortsentryWhitelistIpDto> AddWhitelistIpAsync(int serverId, AddPortsentryWhitelistRequest request, CancellationToken ct = default)
    {
        if (!PortsentryCommandHelper.IsValidIpAddress(request.IpAddress))
            throw new BadRequestException("Invalid IP address.");

        var entity = new PortsentryWhitelistIp
        {
            ServerId = serverId,
            IpAddress = request.IpAddress,
            Description = request.Description
        };

        await repo.AddWhitelistIpAsync(entity, ct).ConfigureAwait(false);
        await audit.LogAsync("PortsentryWhitelistAdd", "Portsentry", serverId, request.IpAddress, ct).ConfigureAwait(false);

        return MapWhitelistIp(entity);
    }

    public async Task<bool> RemoveWhitelistIpAsync(int serverId, int id, CancellationToken ct = default)
    {
        var removed = await repo.RemoveWhitelistIpAsync(id, serverId, ct).ConfigureAwait(false);
        if (removed)
            await audit.LogAsync("PortsentryWhitelistRemove", "Portsentry", serverId, id.ToString(), ct).ConfigureAwait(false);
        return removed;
    }

    private static PortsentryBlockedIpDto MapBlockedIp(PortsentryBlockedIp item) => new()
    {
        IpAddress = item.IpAddress,
        Protocol = item.Protocol,
        BlockedAt = item.BlockedAt,
        Reason = item.Reason
    };

    private static PortsentryWhitelistIpDto MapWhitelistIp(PortsentryWhitelistIp item) => new()
    {
        Id = item.Id,
        IpAddress = item.IpAddress,
        Description = item.Description,
        CreatedAt = item.CreatedAt
    };
}
