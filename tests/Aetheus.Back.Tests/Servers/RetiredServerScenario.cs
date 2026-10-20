// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Servers;

/// <summary>Retirement fixture includes server links, credentials and administrator settings. Use
/// fresh contexts over the same in-memory store to expose query-filter behaviour without relying on
/// tracked state.</summary>
internal sealed class RetiredServerScenario
{
    public const int OrgId = 1;
    public const int OtherOrgId = 2;
    public const int ServerId = 4;
    public const int OtherServerId = 5;
    public const int EnvironmentId = 10;
    public const int PoolId = 20;
    public const int ProjectId = 30;
    public const int ProjectServerId = 40;
    public const string Hostname = "vps2577917";
    public const string EnvironmentName = "Production";
    public const string PoolName = "web-pool";
    public const string OldTokenHash = "old-agent-token-hash";
    public const string SigningKey = "aetheus-dev-key-minimum-32-bytes!!";
    public static readonly string MachineHash = new('a', 64);

    private readonly string _store = Guid.NewGuid().ToString("N");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero));
    public IAuditService Audit { get; } = Substitute.For<IAuditService>();
    public IHubContext<ServerHub> Hub { get; }

    public RetiredServerScenario()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(Substitute.For<IClientProxy>());
        Hub = Substitute.For<IHubContext<ServerHub>>();
        Hub.Clients.Returns(clients);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_store).Options, Clock);

    public ServerRetirementService Retirement(AppDbContext db) =>
        new(new ServerRetirementRepository(db), Hub, Audit, Clock);

    public ServerEnrollmentService Enrollment(AppDbContext db) => new(
        new AuthRepository(db, Clock),
        new ConfigurationBuilder().Build(),
        Audit,
        Hub,
        Substitute.For<IAdminChangeNotifier>(),
        new JwtOptions { SigningKey = SigningKey },
        Clock);

    public static string[] PipelineCapabilities => [AgentCapabilities.PipelineBuild, AgentCapabilities.Deployment];

    public static string CapabilitiesJson =>
        "[" + string.Join(",", PipelineCapabilities.Order(StringComparer.Ordinal).Select(item => $"\"{item}\"")) + "]";

    public static Server RunnerServer(int id, string name, int organizationId = OrgId, string tags = "[]") => new()
    {
        Id = id,
        Name = name,
        Hostname = name,
        OrganizationId = organizationId,
        Status = ServerStatus.Online,
        OsType = OsType.Linux,
        Tags = tags,
        PipelineRunnerEnabled = true,
        AgentProtocolVersion = AgentProtocol.CurrentVersion,
        AgentCapabilitiesJson = CapabilitiesJson
    };

    public async Task SeedAsync()
    {
        await using var db = NewContext();
        var server = RunnerServer(ServerId, Hostname, tags: "[\"prod\",\"web\"]");
        server.MachineIdHash = MachineHash;
        server.RequireContainerIsolation = true;
        db.Servers.AddRange(server, RunnerServer(OtherServerId, "vps-other"));
        db.Environments.Add(new Environment { Id = EnvironmentId, Name = EnvironmentName, ProjectId = ProjectId });
        db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = EnvironmentId, ServerId = ServerId });
        db.AgentPools.Add(new AgentPool { Id = PoolId, Name = PoolName });
        db.AgentPoolServers.Add(new AgentPoolServer { AgentPoolId = PoolId, ServerId = ServerId });
        db.ServerPortReservations.Add(new ServerPortReservation
        {
            ServerId = ServerId,
            Port = 5010,
            OwnerKey = "project:30:shop",
            OwnerLabel = "shop",
            ProjectId = ProjectId,
            Source = PortReservationSource.Declared
        });
        db.ServerPortRanges.Add(new ServerPortRange { ServerId = ServerId, From = 5000, To = 5099 });
        db.Projects.Add(new Project { Id = ProjectId, Name = "shop", OrganizationId = OrgId });
        db.ProjectServers.Add(new ProjectServer
        {
            Id = ProjectServerId,
            ProjectId = ProjectId,
            ServerId = ServerId,
            Type = ProjectServerType.AgentServer,
            DisplayName = "shop on vps2577917",
            Host = Hostname
        });
        db.VariableLibraries.Add(new VariableLibrary { Name = "shop-prod-vars", ProjectServerId = ProjectServerId });
        db.Vaults.Add(new Vault { Name = "shop-prod-secrets", ProjectServerId = ProjectServerId });
        db.ServerTokens.Add(new ServerToken { ServerId = ServerId, TokenHash = OldTokenHash, ExpiresAt = Now.AddDays(300) });
        await db.SaveChangesAsync(Ct);
    }

    public async Task RetireAsync()
    {
        await using var db = NewContext();
        Assert.True(await Retirement(db).RetireServerAsync(ServerId, "operator", Ct));
    }

    /// <summary>Adds a live registration token for <paramref name="organizationId"/> and returns the
    /// plaintext an agent would present.</summary>
    public async Task<string> AddRegistrationTokenAsync(int organizationId = OrgId)
    {
        var plaintext = "reg-" + Guid.NewGuid().ToString("N");
        await using var db = NewContext();
        db.RegistrationTokens.Add(new RegistrationToken
        {
            Token = AuthTokenHelper.HashToken(SigningKey, plaintext),
            OrganizationId = organizationId,
            ExpiresAt = Now.AddDays(1)
        });
        await db.SaveChangesAsync(Ct);
        return plaintext;
    }

    public static ServerRegistrationRequest Request(
        string registrationToken, string hostname, string? machineIdHash, bool pipelineRunnerAvailable = true) => new()
        {
            RegistrationToken = registrationToken,
            Hostname = hostname,
            OsDescription = "Ubuntu 24.04.3 LTS",
            AgentVersion = "1.1.2400",
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilities = [.. PipelineCapabilities],
            IpAddress = "203.0.113.4",
            PipelineRunnerAvailable = pipelineRunnerAvailable,
            MachineIdHash = machineIdHash
        };

    public async Task<ServerRegistrationResponse?> EnrollAsync(ServerRegistrationRequest request)
    {
        await using var db = NewContext();
        return await Enrollment(db).RegisterServerAsync(request, Ct);
    }

    public async Task<Server> LoadIncludingRetiredAsync(int id)
    {
        await using var db = NewContext();
        return await db.Servers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired]).AsNoTracking()
            .SingleAsync(server => server.Id == id, Ct);
    }
}
