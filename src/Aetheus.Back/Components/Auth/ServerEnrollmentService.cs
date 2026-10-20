// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Auth;

// Agent/server enrollment - extracted from AuthService into a focused collaborator (removes the
// former AuthService.ServerRegistration.cs partial split). Shares token primitives with AuthService
// via AuthTokenHelper.
public class ServerEnrollmentService(
    IAuthRepository repo,
    IConfiguration config,
    IAuditService audit,
    IHubContext<ServerHub> serverHub,
    IAdminChangeNotifier adminNotifier,
    JwtOptions jwtOptions,
    TimeProvider timeProvider) : IServerEnrollmentService
{
    public async Task<ServerRegistrationResponse?> RegisterServerAsync(ServerRegistrationRequest request, CancellationToken ct = default)
    {
        // Hardening (High #6): repo lookup now compares the SHA-256 hash, not plaintext, so
        // a DB read does not yield usable registration tokens.
        var regToken = await repo.FindValidRegistrationTokenAsync(AuthTokenHelper.HashToken(jwtOptions.SigningKey, request.RegistrationToken), ct).ConfigureAwait(false);
        if (regToken is null) return null;

        var (server, matchedBy) = await FindEnrollmentMatchAsync(request, regToken.OrganizationId, ct).ConfigureAwait(false);
        if (server is not null && server.OrganizationId != regToken.OrganizationId)
            return null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var revived = server?.DeletedAt is not null;
        if (server is not null)
            ApplyReenrollment(server, request, now);
        else
            server = NewServer(request, regToken.OrganizationId, now);
        var bearerToken = AuthTokenHelper.GenerateSecureToken();
        var tokenHash = AuthTokenHelper.HashToken(jwtOptions.SigningKey, bearerToken);
        var ttlDays = config.GetValue("AgentToken:TtlDays", 365);
        var serverToken = new ServerToken
        {
            Server = server,
            TokenHash = tokenHash,
            ExpiresAt = timeProvider.GetUtcNow().UtcDateTime.AddDays(ttlDays)
        };
        if (!await repo.TryPersistServerEnrollmentAsync(
                regToken.Id,
                server,
                serverToken,
                ct)
            .ConfigureAwait(false))
            return null;

        if (revived)
            await audit.LogAsync("Revived", "Server", server.Id,
                $"{server.Name} ({request.Hostname}) revived on re-enrollment, matched by {matchedBy}; links and settings kept",
                ct).ConfigureAwait(false);
        else
            await audit.LogAsync("Registered", "Server", server.Id, server.Hostname, ct).ConfigureAwait(false);
        // S-FEAT-RT2W: the token just flipped to used - refresh any open admin token list in realtime.
        await adminNotifier.BroadcastAsync(AdminEntities.RegistrationToken, regToken.Id, EntityChangeOps.Updated, ct).ConfigureAwait(false);

        // RBAC scoping (audit 360): every target group is access-filtered by the F-05 fan-out in
        // ServerHub.JoinAllServers - admins (full access) sit in AllServers, server-{id} only contains
        // connections that passed a Read check on this server, and server-org-{org} only contains members
        // of this server's organization (org membership grants Read on every org server). The org group
        // is what reaches a non-admin member in realtime, since server-{id} is empty for a brand-new
        // server. So this broadcast never reaches an unauthorized subscriber. The payload omits IpAddress.
        await serverHub.Clients.Groups([HubGroups.AllServers, HubGroups.Server(server.Id), HubGroups.ServerOrg(server.OrganizationId)]).SendAsync("ServerRegistered", new ServerDto
        {
            Id = server.Id,
            Name = server.Name,
            Hostname = server.Hostname,
            OsDescription = server.OsDescription,
            AgentVersion = server.AgentVersion,
            Status = server.Status,
            Type = server.Type,
            LastHeartbeat = server.LastHeartbeat,
            Tags = TagsHelper.DeserializeTags(server.Tags),
            CreatedAt = server.CreatedAt,
            OrganizationId = server.OrganizationId,
            AgentInstalledAt = server.AgentInstalledAt
        }, ct).ConfigureAwait(false);

        return new ServerRegistrationResponse
        {
            ServerId = server.Id,
            BearerToken = bearerToken
        };
    }

    // Prefer machine-id matching within the enrollment token organisation, including retired rows,
    // so host renaming preserves identity. Fall back to hostname matching across organisations,
    // leaving cross-organisation ownership conflicts for the caller to reject.
    private async Task<(Server? Server, string? MatchedBy)> FindEnrollmentMatchAsync(
        ServerRegistrationRequest request, int organizationId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.MachineIdHash))
        {
            var byMachine = await repo.FindServerByMachineIdHashAsync(
                organizationId, request.MachineIdHash, request.Hostname, ct).ConfigureAwait(false);
            if (byMachine is not null) return (byMachine, "machine-id");
        }

        var byHostname = await repo.FindServerByHostnameAsync(request.Hostname, ct).ConfigureAwait(false);
        return (byHostname, byHostname is null ? null : "hostname");
    }

    // Updates the runtime facts the agent reports and revives a retired row. The row's identity (Name,
    // Hostname: a machine-id match may come with a new hostname that another row already owns),
    // everything an admin set (Tags, type, PipelineRunnerEnabled, RequireContainerIsolation) and every
    // link stay as they were: the pipeline-runner gate follows the admin, while the reported
    // capabilities below still decide what the scheduler may dispatch here.
    private static void ApplyReenrollment(Server server, ServerRegistrationRequest request, DateTime now)
    {
        server.DeletedAt = null;
        server.OsDescription = request.OsDescription;
        server.OsType = OsTypeHelper.FromDescription(request.OsDescription);
        server.IpAddress = request.IpAddress;
        server.AgentVersion = request.AgentVersion;
        server.AgentProtocolVersion = request.AgentProtocolVersion;
        server.AgentCapabilitiesJson = SerializeCapabilities(request);
        server.AgentInstalledAt = request.AgentInstalledAt;
        server.DockerAvailable = request.DockerAvailable;
        server.InsecureTls = request.InsecureTls;
        server.MachineIdHash = request.MachineIdHash ?? server.MachineIdHash;
        server.Status = ServerStatus.Online;
        server.LastHeartbeat = now;
        server.UpdatedAt = now;
    }

    private static Server NewServer(ServerRegistrationRequest request, int organizationId, DateTime now) => new()
    {
        Name = request.Hostname,
        Hostname = request.Hostname,
        OsDescription = request.OsDescription,
        OsType = OsTypeHelper.FromDescription(request.OsDescription),
        IpAddress = request.IpAddress,
        AgentVersion = request.AgentVersion,
        AgentProtocolVersion = request.AgentProtocolVersion,
        AgentCapabilitiesJson = SerializeCapabilities(request),
        AgentInstalledAt = request.AgentInstalledAt,
        DockerAvailable = request.DockerAvailable,
        InsecureTls = request.InsecureTls,
        MachineIdHash = request.MachineIdHash,
        Status = ServerStatus.Online,
        LastHeartbeat = now,
        OrganizationId = organizationId,
        // Default the runner gate from the reported toolchain instead of authorising every box. A
        // request without the current capability contract is not eligible to execute pipelines.
        PipelineRunnerEnabled = IsPipelineRunnerEligible(request)
    };

    private static string? SerializeCapabilities(ServerRegistrationRequest request) =>
        request.AgentCapabilities is null
            ? null
            : JsonSerializer.Serialize(request.AgentCapabilities
                .Where(capability => !string.IsNullOrWhiteSpace(capability))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

    private static bool IsPipelineRunnerEligible(ServerRegistrationRequest request) =>
        request.PipelineRunnerAvailable == true
        && request.AgentProtocolVersion is { } protocol
        && AgentProtocol.IsSupported(protocol)
        && request.AgentCapabilities?.Contains(AgentCapabilities.PipelineBuild, StringComparer.Ordinal) == true;
}
