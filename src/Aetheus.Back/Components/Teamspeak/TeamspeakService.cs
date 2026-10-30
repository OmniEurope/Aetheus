// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Teamspeak;

public class TeamspeakService(ITeamspeakRepository repo, IServerRepository serverRepo, IAuditService audit, IEncryptionService encryption, ITaskService taskService) : ITeamspeakService
{
    private Task QueueTaskAsync(ServerTask task, CancellationToken ct = default)
        => TaskQueuePersistence.PersistAndNotifyAsync(repo.AddTaskAsync, taskService, task, ct);

    // S-TECH-W9K7: stand-in stored in the plaintext-at-rest Command column for secret-bearing
    // ServerQuery actions; the real command rides in the encrypted TEAMSPEAK_QUERY_CMD env var.
    private const string RedactedQueryCommand = "(redacted - sensitive ServerQuery)";

    // S-TECH-87: dispatch a single TeamSpeak ServerQuery command as a typed TeamspeakServerQuery
    // operation (native TCP on the agent - no shell, no `nc`). The query port rides in the
    // TEAMSPEAK_QUERY_PORT env var; the agent supplies the `login serveradmin <cred>` from its local
    // credentials file, so no credential ever travels in the task.
    private Task AddServerQueryTaskAsync(int serverId, string name, string command, int queryPort, int timeoutSeconds, CancellationToken ct, bool sensitive = false)
    {
        var env = new Dictionary<string, string> { ["TEAMSPEAK_QUERY_PORT"] = queryPort.ToString(CultureInfo.InvariantCulture) };

        // S-TECH-W9K7: secret-bearing commands (channel/server password, token value, snapshot blob)
        // must not sit in the plaintext-at-rest Command column. Move the command into the encrypted
        // EnvironmentVariables (AES at rest, decrypted only when dispatched to the agent) and leave a
        // redaction marker in Command. Non-secret commands keep the readable Command + plaintext port.
        var storedCommand = sensitive ? RedactedQueryCommand : command;
        if (sensitive)
            env["TEAMSPEAK_QUERY_CMD"] = command;

        var task = ServerTaskFactory.Operation(serverId, name, OperationKind.TeamspeakServerQuery, storedCommand, timeoutSeconds);
        task.EnvironmentVariables = sensitive
            ? TaskEnvProtection.Protect(encryption, JsonSerializer.Serialize(env))
            : JsonSerializer.Serialize(env);
        return QueueTaskAsync(task, ct);
    }

    public async Task<TeamspeakDataDto> GetStateAsync(int serverId, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        if (state is null)
            return new TeamspeakDataDto();

        return new TeamspeakDataDto
        {
            IsInstalled = true,
            IsRunning = state.IsRunning,
            Version = state.Version,
            Platform = state.Platform,
            ServerName = state.ServerName,
            VoicePort = state.VoicePort,
            QueryPort = state.QueryPort,
            MaxClients = state.MaxClients,
            OnlineClients = state.OnlineClients,
            ChannelCount = state.ChannelCount,
            UptimeSeconds = state.UptimeSeconds
        };
    }

    public async Task<PaginatedResult<TeamspeakChannelDto>> GetChannelsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetChannelsPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<TeamspeakChannelDto>
        {
            Items = items.Select(channel => new TeamspeakChannelDto
            {
                Id = channel.ChannelId,
                Name = channel.Name,
                ParentId = channel.ParentId,
                Order = channel.Order,
                TotalClients = channel.TotalClients,
                MaxClients = channel.MaxClients,
                IsDefault = channel.IsDefault,
                HasPassword = channel.HasPassword,
                IsPermanent = channel.IsPermanent
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PaginatedResult<TeamspeakClientDto>> GetClientsAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetClientsPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<TeamspeakClientDto>
        {
            Items = items.Select(client => new TeamspeakClientDto
            {
                ClientId = client.ClientId,
                UniqueId = client.UniqueId,
                Nickname = client.Nickname,
                ChannelId = client.ChannelId,
                ChannelName = client.ChannelName,
                Platform = client.Platform,
                Version = client.Version,
                IdleTimeSeconds = client.IdleTimeSeconds,
                ConnectionTimeSeconds = client.ConnectionTimeSeconds,
                IsServerQuery = client.IsServerQuery
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>What the clients grid's checkable Platform filter offers.</summary>
    public async Task<TeamspeakFilterValuesDto> GetFilterValuesAsync(int serverId, CancellationToken ct = default) =>
        new() { Platforms = await repo.GetClientPlatformsAsync(serverId, ct).ConfigureAwait(false) };

    public async Task<PaginatedResult<TeamspeakBanDto>> GetBansAsync(
        int serverId, PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (items, total) = await repo.GetBansPagedAsync(
            serverId, request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<TeamspeakBanDto>
        {
            Items = items.Select(ban => new TeamspeakBanDto
            {
                BanId = ban.BanId,
                Ip = ban.Ip,
                UniqueId = ban.UniqueId,
                Nickname = ban.Nickname,
                Reason = ban.Reason,
                Duration = ban.Duration,
                Created = ban.Created
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task ExecuteActionAsync(int serverId, TeamspeakActionRequest request, CancellationToken ct = default)
    {
        // R-119: a typed operation (the agent resolves the unit and runs the argv-exact systemctl grant); its kind
        // makes the agent collect its TeamSpeak inventory again at once.
        var operation = request.Action switch
        {
            TeamspeakAction.Start => OperationKind.TeamspeakStart,
            TeamspeakAction.Stop => OperationKind.TeamspeakStop,
            TeamspeakAction.Restart => OperationKind.TeamspeakRestart,
            _ => throw new BadRequestException($"Unknown TeamSpeak action: {request.Action}")
        };

        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, $"TeamSpeak - {request.Action}", operation,
            target: "-", timeoutSeconds: 60), ct).ConfigureAwait(false);

        await audit.LogAsync($"Teamspeak{request.Action}", "Teamspeak", serverId, request.Action.ToString(), ct).ConfigureAwait(false);
    }

    public async Task SetupAsync(int serverId, TeamspeakSetupRequest request, CancellationToken ct = default)
    {
        if (!TeamspeakCommandHelper.IsValidInstallPath(request.InstallPath))
            throw new BadRequestException("Invalid install path.");
        if (request.VoicePort is < 1 or > 65535 || request.QueryPort is < 1 or > 65535)
            throw new BadRequestException("Voice and query ports must be between 1 and 65535.");

        // Gate on the server's reported teamspeak-setup capability so the UI never queues an action the
        // agent cannot perform - without the aetheus-teamspeak sudoers grant the helper's `sudo -n`
        // would refuse in silence. Mirrors the mail-setup / package-manage gates.
        var server = await serverRepo.FindServerAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Server {serverId} not found.");
        if (!server.TeamspeakSetupAvailable)
            throw new BadRequestException(
                "TeamSpeak install is not enabled on this server. Re-run the agent installer with the " +
                "server-management module (or --enable-teamspeak-setup) to grant the controlled-sudo helper.");

        // Dispatch TeamspeakSetup through the root-owned helper with the exact sudoers grant. Pass
        // installation path as target and ports through environment variables.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TeamspeakSetupEnv.VoicePort] = request.VoicePort.ToString(CultureInfo.InvariantCulture),
            [TeamspeakSetupEnv.QueryPort] = request.QueryPort.ToString(CultureInfo.InvariantCulture)
        };

        var task = ServerTaskFactory.Operation(serverId, "TeamSpeak - install",
            OperationKind.TeamspeakSetup, request.InstallPath, env, timeoutSeconds: 300);
        await QueueTaskAsync(task, ct).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakSetup", "Teamspeak", serverId, request.InstallPath, ct).ConfigureAwait(false);
    }

    public async Task GetLogsAsync(int serverId, TeamspeakLogRequest request, CancellationToken ct = default)
    {
        // Switched from a shell `tail ... | sort | tail` (rejected by the CommandValidator) to a typed
        // read: the agent tails the newest file under {installPath}/logs/ directly. The requested line
        // count is bounded agent-side (fixed cap); the install path is the target.
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false);
        var installPath = state?.InstallPath ?? string.Empty;
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId, "TeamSpeak - logs",
            OperationKind.TeamspeakGetLogs, target: installPath, timeoutSeconds: 15), ct).ConfigureAwait(false);
    }

    public async Task KickClientAsync(int serverId, TeamspeakKickRequest request, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildKickCommand(request.ClientId, request.ReasonMessage, state.QueryPort);

        await AddServerQueryTaskAsync(serverId, $"TeamSpeak - kick client {request.ClientId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakKick", "Teamspeak", serverId, $"Client {request.ClientId}", ct).ConfigureAwait(false);
    }

    public async Task BanClientAsync(int serverId, TeamspeakBanRequest request, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildBanCommand(request.ClientUniqueId, request.DurationSeconds, request.Reason, state.QueryPort);

        await AddServerQueryTaskAsync(serverId, $"TeamSpeak - ban {request.ClientUniqueId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakBan", "Teamspeak", serverId, request.ClientUniqueId, ct).ConfigureAwait(false);
    }

    public async Task MoveClientAsync(int serverId, TeamspeakMoveClientRequest request, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildMoveClientCommand(
            request.ClientId, request.TargetChannelId, request.ChannelPassword, state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            $"TeamSpeak - move client {request.ClientId} → channel {request.TargetChannelId}", command, state.QueryPort, 15, ct,
            sensitive: !string.IsNullOrEmpty(request.ChannelPassword)).ConfigureAwait(false);
        await audit.LogAsync("TeamspeakMove", "Teamspeak", serverId,
            $"Client {request.ClientId} → Channel {request.TargetChannelId}", ct).ConfigureAwait(false);
    }

    public async Task PokeClientAsync(int serverId, TeamspeakPokeClientRequest request, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildPokeClientCommand(request.ClientId, request.Message, state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            $"TeamSpeak - poke client {request.ClientId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);
        // The poke MESSAGE may contain sensitive ops info - log only the recipient, not the body.
        await audit.LogAsync("TeamspeakPoke", "Teamspeak", serverId, $"Client {request.ClientId}", ct).ConfigureAwait(false);
    }

    // ===== Tier 2/3 =====

    private async Task<TeamspeakState> RequireStateAsync(int serverId, CancellationToken ct)
    {
        return await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");
    }

    public async Task GetClientInfoAsync(int serverId, TeamspeakClientInfoRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildClientInfoCommand(request.ClientId, state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            $"TeamSpeak - clientinfo {request.ClientId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);
        await audit.LogAsync("TeamspeakClientInfo", "Teamspeak", serverId, $"Client {request.ClientId}", ct).ConfigureAwait(false);
    }

    public async Task GracefulRestartAsync(int serverId, TeamspeakGracefulRestartRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var seconds = Math.Clamp(request.WarningSeconds, 0, 600);
        var message = string.Format(request.WarningMessage, seconds);

        // Switched from a shell `printf|nc && sleep && ... && systemctl restart teamspeak3` (rejected by
        // the CommandValidator, and even targeted the wrong unit name) to a typed composite op: the agent
        // broadcasts the warning + kicks over the native ServerQuery TCP client, then restarts the
        // ts3server unit via the service-control sudoers grant. Warning params + query port travel in env.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TeamspeakSetupEnv.RuntimeQueryPort] = state.QueryPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [TeamspeakSetupEnv.WarnSeconds] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [TeamspeakSetupEnv.WarnMessage] = message
        };
        // Timeout = warning + 60 s slack for the actual restart
        await QueueTaskAsync(ServerTaskFactory.Operation(serverId,
            $"TeamSpeak - graceful restart ({seconds}s warn)", OperationKind.TeamspeakGracefulRestart,
            target: "-", env, timeoutSeconds: seconds + 60), ct).ConfigureAwait(false);
        await audit.LogAsync("TeamspeakGracefulRestart", "Teamspeak", serverId, $"warn={seconds}s", ct).ConfigureAwait(false);
    }

    public async Task CreateSnapshotAsync(int serverId, TeamspeakSnapshotCreateRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildSnapshotCreateCommand(state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            $"TeamSpeak - snapshot{(request.Label is null ? string.Empty : $" - {request.Label}")}", command, state.QueryPort, 60, ct).ConfigureAwait(false);
        await audit.LogAsync("TeamspeakSnapshotCreate", "Teamspeak", serverId, request.Label ?? "(unnamed)", ct).ConfigureAwait(false);
    }

    public async Task DeploySnapshotAsync(int serverId, TeamspeakSnapshotDeployRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        // Operator must type the server name verbatim. Cheap fat-finger guard for a destructive op.
        if (!string.Equals(request.Confirmation, state.ServerName, StringComparison.Ordinal))
            throw new BadRequestException("Confirmation does not match the server name.");

        var command = TeamspeakCommandHelper.BuildSnapshotDeployCommand(request.SnapshotBlob, state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            "TeamSpeak - snapshot DEPLOY", command, state.QueryPort, 120, ct, sensitive: true).ConfigureAwait(false);
        await audit.LogAsync("TeamspeakSnapshotDeploy", "Teamspeak", serverId,
            $"blob={request.SnapshotBlob.Length} bytes", ct).ConfigureAwait(false);
    }

    public async Task ListServerGroupsAsync(int serverId, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildServerGroupListCommand(state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            "TeamSpeak - servergrouplist", command, state.QueryPort, 15, ct).ConfigureAwait(false);
    }

    public async Task AddClientToServerGroupAsync(int serverId, TeamspeakServerGroupAddRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildServerGroupAddClientCommand(
            request.ServerGroupId, request.ClientDatabaseId, state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            $"TeamSpeak - add cldbid {request.ClientDatabaseId} → sgid {request.ServerGroupId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);
        await audit.LogAsync("TeamspeakServerGroupAdd", "Teamspeak", serverId,
            $"sgid={request.ServerGroupId} cldbid={request.ClientDatabaseId}", ct).ConfigureAwait(false);
    }

    public async Task RemoveClientFromServerGroupAsync(int serverId, TeamspeakServerGroupRemoveRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildServerGroupDelClientCommand(
            request.ServerGroupId, request.ClientDatabaseId, state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            $"TeamSpeak - remove cldbid {request.ClientDatabaseId} from sgid {request.ServerGroupId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);
        await audit.LogAsync("TeamspeakServerGroupRemove", "Teamspeak", serverId,
            $"sgid={request.ServerGroupId} cldbid={request.ClientDatabaseId}", ct).ConfigureAwait(false);
    }

    public async Task ListTokensAsync(int serverId, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildTokenListCommand(state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            "TeamSpeak - tokenlist", command, state.QueryPort, 15, ct).ConfigureAwait(false);
    }

    public async Task CreateTokenAsync(int serverId, TeamspeakTokenCreateRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildTokenAddCommand(
            request.Type, request.GroupId, request.ChannelId, request.Description, state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            $"TeamSpeak - tokenadd type={request.Type} group={request.GroupId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);
        // The token VALUE is sensitive (it grants admin) - don't log the description either,
        // it can contain hints. Log only the type + group reference.
        await audit.LogAsync("TeamspeakTokenAdd", "Teamspeak", serverId,
            $"type={request.Type} group={request.GroupId}", ct).ConfigureAwait(false);
    }

    public async Task DeleteTokenAsync(int serverId, TeamspeakTokenDeleteRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildTokenDeleteCommand(request.Token, state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            "TeamSpeak - tokendelete", command, state.QueryPort, 15, ct, sensitive: true).ConfigureAwait(false);
        // Never log the token value itself.
        await audit.LogAsync("TeamspeakTokenDelete", "Teamspeak", serverId,
            $"len={request.Token.Length}", ct).ConfigureAwait(false);
    }

    public async Task GetServerInfoAsync(int serverId, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildServerInfoCommand(state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            "TeamSpeak - serverinfo", command, state.QueryPort, 15, ct).ConfigureAwait(false);
    }

    public async Task ListComplaintsAsync(int serverId, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildComplaintListCommand(state.QueryPort);
        await AddServerQueryTaskAsync(serverId,
            "TeamSpeak - complainlist", command, state.QueryPort, 15, ct).ConfigureAwait(false);
    }

    public async Task DeleteComplaintAsync(int serverId, TeamspeakComplaintDeleteRequest request, CancellationToken ct = default)
    {
        var state = await RequireStateAsync(serverId, ct).ConfigureAwait(false);
        var command = TeamspeakCommandHelper.BuildComplaintDeleteCommand(
            request.TargetClientDatabaseId, request.SourceClientDatabaseId, state.QueryPort);
        var label = request.SourceClientDatabaseId is null
            ? $"all complaints for cldbid {request.TargetClientDatabaseId}"
            : $"complaint cldbid {request.SourceClientDatabaseId} → {request.TargetClientDatabaseId}";
        await AddServerQueryTaskAsync(serverId,
            $"TeamSpeak - complaint delete ({label})", command, state.QueryPort, 15, ct).ConfigureAwait(false);
        await audit.LogAsync("TeamspeakComplaintDelete", "Teamspeak", serverId, label, ct).ConfigureAwait(false);
    }

    public async Task UnbanAsync(int serverId, int banId, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildUnbanCommand(banId, state.QueryPort);

        await AddServerQueryTaskAsync(serverId, $"TeamSpeak - unban {banId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakUnban", "Teamspeak", serverId, $"Ban {banId}", ct).ConfigureAwait(false);
    }

    public async Task CreateChannelAsync(int serverId, TeamspeakCreateChannelRequest request, CancellationToken ct = default)
    {
        if (!TeamspeakCommandHelper.IsValidChannelName(request.Name))
            throw new BadRequestException("Invalid channel name.");

        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildCreateChannelCommand(
            request.Name, request.ParentId, request.Password, request.MaxClients, request.IsPermanent, state.QueryPort);

        await AddServerQueryTaskAsync(serverId, $"TeamSpeak - create channel {request.Name}", command, state.QueryPort, 15, ct,
            sensitive: !string.IsNullOrEmpty(request.Password)).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakCreateChannel", "Teamspeak", serverId, request.Name, ct).ConfigureAwait(false);
    }

    public async Task EditChannelAsync(int serverId, TeamspeakEditChannelRequest request, CancellationToken ct = default)
    {
        if (request.Name is not null && !TeamspeakCommandHelper.IsValidChannelName(request.Name))
            throw new BadRequestException("Invalid channel name.");

        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildEditChannelCommand(
            request.ChannelId, request.Name, request.Password, request.MaxClients, state.QueryPort);

        await AddServerQueryTaskAsync(serverId, $"TeamSpeak - edit channel {request.ChannelId}", command, state.QueryPort, 15, ct,
            sensitive: !string.IsNullOrEmpty(request.Password)).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakEditChannel", "Teamspeak", serverId, $"Channel {request.ChannelId}", ct).ConfigureAwait(false);
    }

    public async Task DeleteChannelAsync(int serverId, int channelId, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildDeleteChannelCommand(channelId, state.QueryPort);

        await AddServerQueryTaskAsync(serverId, $"TeamSpeak - delete channel {channelId}", command, state.QueryPort, 15, ct).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakDeleteChannel", "Teamspeak", serverId, $"Channel {channelId}", ct).ConfigureAwait(false);
    }

    public async Task EditServerAsync(int serverId, TeamspeakServerEditRequest request, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildServerEditCommand(
            request.ServerName, request.Password, request.MaxClients, request.WelcomeMessage, state.QueryPort);

        await AddServerQueryTaskAsync(serverId, "TeamSpeak - edit server", command, state.QueryPort, 15, ct,
            sensitive: !string.IsNullOrEmpty(request.Password)).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakEditServer", "Teamspeak", serverId, request.ServerName ?? "settings", ct).ConfigureAwait(false);
    }

    public async Task SendGlobalMessageAsync(int serverId, TeamspeakGlobalMessageRequest request, CancellationToken ct = default)
    {
        var state = await repo.GetStateAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new BadRequestException("TeamSpeak is not installed on this server.");

        var command = TeamspeakCommandHelper.BuildGlobalMessageCommand(request.Message, state.QueryPort);

        await AddServerQueryTaskAsync(serverId, "TeamSpeak - global message", command, state.QueryPort, 15, ct).ConfigureAwait(false);

        await audit.LogAsync("TeamspeakMessage", "Teamspeak", serverId, "Global message", ct).ConfigureAwait(false);
    }
}
