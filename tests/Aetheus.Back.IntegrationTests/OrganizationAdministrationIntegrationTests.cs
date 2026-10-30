// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// ADR-059 security review, on the real host and PostgreSQL with the real IsOrganizationAdminAsync:
/// an Organization Admin of A manages A's members only and cannot walk the global user ids (S2, S4a);
/// the last Owner stays (M6); a concurrent role assignment is a 409, not a 500 (M5); nothing links a
/// resource of another organization (M4); EntityHub and PipelineHub never deliver A's events to a
/// user of B only (S4b).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationAdministrationIntegrationTests(PostgresFixture fixture)
{
    private static readonly HttpStatusCode[] Refused = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound];

    [Fact]
    public async Task OrganizationAdminOfA_CannotAddByGlobalId_NorTouchOrganizationBMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var orgAdminA = await CreateUserAsync(factory, "oadm", (a.OrganizationId, "Organization Admin", OrganizationRole.Owner));
        var memberOfB = await CreateUserAsync(factory, "memb", (b.OrganizationId, "Reader", OrganizationRole.Member));
        var contributorA = await CreateUserAsync(factory, "contr", (a.OrganizationId, "Contributor", OrganizationRole.Maintainer));
        var outsider = await CreateUserAsync(factory, "outs");
        using var client = await IntegrationUsers.LoginClientAsync(factory, orgAdminA.Username);

        // S2: no attaching by a raw global id, which would read back a stranger's username and e-mail.
        var byId = await client.PostAsJsonAsync($"/api/organizations/{a.OrganizationId}/members",
            new AddOrganizationMemberRequest { UserId = outsider.Id, Role = OrganizationRole.Member }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, byId.StatusCode);
        Assert.DoesNotContain(outsider.Username, await byId.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.False(await IsMemberAsync(factory, outsider.Id, a.OrganizationId));
        // A login nobody has is a plain 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/organizations/{a.OrganizationId}/members",
            new AddOrganizationMemberRequest { UserLogin = $"nobody-{Guid.NewGuid():N}", Role = OrganizationRole.Member }, ct)).StatusCode);
        // By exact username it works, and the response does not read back the e-mail of the user found.
        var byLogin = await client.PostAsJsonAsync($"/api/organizations/{a.OrganizationId}/members",
            new AddOrganizationMemberRequest { UserLogin = outsider.Username, Role = OrganizationRole.Member }, ct);
        Assert.Equal(HttpStatusCode.OK, byLogin.StatusCode);
        var added = await byLogin.Content.ReadFromJsonAsync<OrganizationMemberDto>(IntegrationJsonOptions.Default, ct);
        Assert.Equal(outsider.Id, added!.UserId);
        Assert.Null(added.Email);
        Assert.True(await IsMemberAsync(factory, outsider.Id, a.OrganizationId));

        // S4a: organization B's members are out of reach, whichever organization the URL names.
        var memberOfBId = await MemberIdAsync(factory, memberOfB.Id, b.OrganizationId);
        Assert.Contains((await client.GetAsync($"/api/organizations/{b.OrganizationId}", ct)).StatusCode, Refused);
        Assert.Contains((await client.PostAsJsonAsync($"/api/organizations/{b.OrganizationId}/members",
            new AddOrganizationMemberRequest { UserId = outsider.Id, Role = OrganizationRole.Member }, ct)).StatusCode, Refused);
        Assert.Contains((await client.PutAsJsonAsync($"/api/organizations/{b.OrganizationId}/members/{memberOfBId}",
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Owner }, ct)).StatusCode, Refused);
        Assert.Contains((await client.DeleteAsync($"/api/organizations/{b.OrganizationId}/members/{memberOfBId}", ct)).StatusCode, Refused);
        // B's member id under A's URL is not found: member rows are looked up within the URL's organization.
        Assert.Contains((await client.PutAsJsonAsync($"/api/organizations/{a.OrganizationId}/members/{memberOfBId}",
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Owner }, ct)).StatusCode, Refused);
        Assert.Contains((await client.DeleteAsync($"/api/organizations/{a.OrganizationId}/members/{memberOfBId}", ct)).StatusCode, Refused);
        Assert.Equal(OrganizationRole.Member, await LevelAsync(factory, memberOfB.Id, b.OrganizationId));

        // Positive control: it manages its own organization's members.
        var contributorAId = await MemberIdAsync(factory, contributorA.Id, a.OrganizationId);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/organizations/{a.OrganizationId}/members/{contributorAId}",
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Member }, ct)).StatusCode);

        // A Contributor of A is no Organization Admin: the real IsOrganizationAdminAsync refuses it.
        using var contributor = await IntegrationUsers.LoginClientAsync(factory, contributorA.Username);
        Assert.Equal(HttpStatusCode.Forbidden, (await contributor.GetAsync($"/api/organizations/{a.OrganizationId}", ct)).StatusCode);
    }

    [Fact]
    public async Task LastOwner_IsNeverRemovedNorDemoted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var owner = await CreateUserAsync(factory, "own1", (a.OrganizationId, "Organization Admin", OrganizationRole.Owner));
        var second = await CreateUserAsync(factory, "own2", (a.OrganizationId, "Reader", OrganizationRole.Member));
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        var ownerId = await MemberIdAsync(factory, owner.Id, a.OrganizationId);
        var secondId = await MemberIdAsync(factory, second.Id, a.OrganizationId);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/organizations/{a.OrganizationId}/members/{ownerId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/organizations/{a.OrganizationId}/members/{ownerId}",
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Member }, ct)).StatusCode);
        Assert.Equal(OrganizationRole.Owner, await LevelAsync(factory, owner.Id, a.OrganizationId));

        // Once another Owner exists, the first one can step down.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/organizations/{a.OrganizationId}/members/{secondId}",
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Owner }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/organizations/{a.OrganizationId}/members/{ownerId}",
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Member }, ct)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRoleAssignment_HittingTheUniqueIndex_IsAConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var user = await CreateUserAsync(factory, "race");
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var readerId = await first.Roles.Where(role => role.Name == "Reader").Select(role => role.Id).SingleAsync(ct);

        // The first request has decided to add the role; the second one saves the same row first.
        var repository = new OrganizationRoleAssignmentRepository(first);
        repository.Add(new UserOrganizationRole { UserId = user.Id, OrganizationId = a.OrganizationId, RoleId = readerId });
        second.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = user.Id, OrganizationId = a.OrganizationId, RoleId = readerId });
        await second.SaveChangesAsync(ct);

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.ConflictException>(() => repository.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task ContributorOfBothOrganizations_CannotLinkAResourceOfTheOtherOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var both = await CreateUserAsync(factory, "both",
            (a.OrganizationId, "Contributor", OrganizationRole.Maintainer), (b.OrganizationId, "Contributor", OrganizationRole.Maintainer));
        var onlyA = await CreateUserAsync(factory, "onlya", (a.OrganizationId, "Contributor", OrganizationRole.Maintainer));
        using var client = await IntegrationUsers.LoginClientAsync(factory, both.Username);

        // Vaults and libraries hold one owner only ([AtMostOneOwner]): a project and another
        // organization's environment together are a 400, and an environment alone is a project-less
        // resource that only a site Admin creates (CanCreateInProjectAsync), so no cross link exists.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/vaults", new CreateVaultRequest
        {
            Name = $"cross-{Guid.NewGuid():N}",
            ProjectId = a.ProjectId,
            EnvironmentId = b.EnvironmentId
        }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/vaults", new CreateVaultRequest
        {
            Name = $"cross-{Guid.NewGuid():N}",
            EnvironmentId = b.EnvironmentId
        }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/variable-libraries", new CreateVariableLibraryRequest
        {
            Name = $"cross-{Guid.NewGuid():N}",
            EnvironmentId = b.EnvironmentId
        }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/environments", new CreateEnvironmentRequest
        {
            Name = $"cross-{Guid.NewGuid():N}",
            ProjectId = a.ProjectId,
            ServerIds = [b.ServerId]
        }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/git/connections", new CreateGitConnectionRequest
        {
            ProjectId = a.ProjectId,
            OwnerOrGroup = "acme",
            RepositoryName = "cross",
            ServiceConnectionId = b.ServiceConnectionId
        }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/servers/{a.ServerId}/ports/allocate",
            new AllocatePortsRequest { Count = 1, OwnerLabel = "cross", ProjectId = b.ProjectId }, ct)).StatusCode);

        // A Contributor of A only cannot reserve ports for B's project either (no Project Write there).
        using var clientA = await IntegrationUsers.LoginClientAsync(factory, onlyA.Username);
        Assert.Equal(HttpStatusCode.Forbidden, (await clientA.PostAsJsonAsync($"/api/servers/{a.ServerId}/ports/allocate",
            new AllocatePortsRequest { Count = 1, OwnerLabel = "cross", ProjectId = b.ProjectId }, ct)).StatusCode);

        // Positive control: the same links inside one organization are accepted.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/environments", new CreateEnvironmentRequest
        {
            Name = $"same-{Guid.NewGuid():N}",
            ProjectId = a.ProjectId,
            ServerIds = [a.ServerId]
        }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/servers/{a.ServerId}/ports/allocate",
            new AllocatePortsRequest { Count = 1, OwnerLabel = "same", ProjectId = a.ProjectId }, ct)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Vaults.AnyAsync(vault => vault.EnvironmentId == b.EnvironmentId, ct));
        Assert.False(await db.GitConnections.AnyAsync(connection => connection.ServiceConnectionId == b.ServiceConnectionId, ct));
    }

    [Fact]
    public async Task EntityHubAndPipelineHub_NeverDeliverOrganizationAEvents_ToAUserOfBOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var userB = await CreateUserAsync(factory, "hubs", (b.OrganizationId, "Contributor", OrganizationRole.Maintainer));
        using var login = factory.CreateClient();
        var token = await IntegrationUsers.LoginTokenAsync(factory, login, userB.Username);

        await using var entities = Connect(factory, "hubs/entities", token);
        var entityIds = new List<int>();
        var ownEntity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entities.On<int, int, string>("EntityChanged", (_, id, _) =>
        {
            lock (entityIds) entityIds.Add(id);
            if (id == b.PipelineId) ownEntity.TrySetResult();
        });
        await entities.StartAsync(ct);
        await entities.InvokeAsync("JoinEntityUpdates", ResourceType.Pipeline, ct);

        await using var pipelines = Connect(factory, "hubs/pipelines", token);
        var pipelineIds = new List<int>();
        var ownPipeline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipelines.On<int>("PipelineUpdated", id =>
        {
            lock (pipelineIds) pipelineIds.Add(id);
            if (id == b.PipelineId) ownPipeline.TrySetResult();
        });
        await pipelines.StartAsync(ct);
        await pipelines.InvokeAsync("JoinPipelineUpdatesGroup", ct);
        // A's run group is refused outright.
        await Assert.ThrowsAsync<HubException>(() => pipelines.InvokeAsync("JoinPipelineRunGroup", a.RunId, ct));

        // The real notifier fans out to every group a reader of the pipeline may be in; A's first.
        var notifier = factory.Services.GetRequiredService<IEntityChangeNotifier>();
        await notifier.BroadcastAsync(ResourceType.Pipeline, a.PipelineId, "Updated", ct, a.OrganizationId);
        await notifier.BroadcastAsync(ResourceType.Pipeline, b.PipelineId, "Updated", ct, b.OrganizationId);
        var pipelineHub = factory.Services.GetRequiredService<IHubContext<PipelineHub>>();
        foreach (var id in new[] { a.PipelineId, b.PipelineId })
            await pipelineHub.Clients.Groups([HubGroups.PipelineUpdates, $"pipeline-{id}"]).SendAsync("PipelineUpdated", id, ct);

        await ownEntity.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await ownPipeline.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        lock (entityIds) Assert.Equal([b.PipelineId], entityIds.Distinct());
        lock (pipelineIds) Assert.Equal([b.PipelineId], pipelineIds.Distinct());
    }

    // S3: webhook and scheduler runs have no principal; the launcher authorizes the pipeline owner by
    // Server.Admin on each target server only (PipelineRunLauncher, F-EXEC-1b), never by a permission
    // on the pipeline, so a project-less pipeline keeps running for an owner who administers the
    // target servers' organization.
    [Fact]
    public async Task AutomatedRunGate_JudgesTheOwnerOnTheTargetServersOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedTenantAsync(factory, "a");
        var b = await SeedTenantAsync(factory, "b");
        var owner = await CreateUserAsync(factory, "sched", (a.OrganizationId, "Organization Admin", OrganizationRole.Owner));
        using var scope = factory.Services.CreateScope();
        var authz = scope.ServiceProvider.GetRequiredService<IResourceAuthorizationService>();

        Assert.True(await authz.HasPermissionAsync(owner.Username, ResourceType.Server, a.ServerId, Permission.Admin, ct));
        Assert.False(await authz.HasPermissionAsync(owner.Username, ResourceType.Server, b.ServerId, Permission.Admin, ct));
    }

    private sealed record Tenant(
        int OrganizationId, int ProjectId, int ServerId, int EnvironmentId, int ServiceConnectionId, int PipelineId, int RunId);

    private static HubConnection Connect(AetheusWebApplicationFactory factory, string path, string token) =>
        new HubConnectionBuilder()
            .WithUrl($"{factory.Server.BaseAddress}{path}", options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private static async Task<bool> IsMemberAsync(AetheusWebApplicationFactory factory, int userId, int organizationId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OrganizationMembers
            .AnyAsync(item => item.UserId == userId && item.OrganizationId == organizationId, TestContext.Current.CancellationToken);
    }

    private static async Task<int> MemberIdAsync(AetheusWebApplicationFactory factory, int userId, int organizationId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OrganizationMembers
            .Where(item => item.UserId == userId && item.OrganizationId == organizationId)
            .Select(item => item.Id).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<OrganizationRole> LevelAsync(AetheusWebApplicationFactory factory, int userId, int organizationId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OrganizationMembers.AsNoTracking()
            .Where(item => item.UserId == userId && item.OrganizationId == organizationId)
            .Select(item => item.Role).SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A user holding each built-in role for its organization, as a member at the given level.</summary>
    private static async Task<(int Id, string Username)> CreateUserAsync(
        AetheusWebApplicationFactory factory, string prefix, params (int OrganizationId, string Role, OrganizationRole Level)[] holds)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Username = $"{prefix}-{Guid.NewGuid():N}",
            Email = $"{prefix}-{Guid.NewGuid():N}@example.test",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(IntegrationUsers.Password),
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        foreach (var (organizationId, roleName, level) in holds)
        {
            var roleId = await db.Roles.Where(role => role.Name == roleName).Select(role => role.Id).SingleAsync(ct);
            db.UserOrganizationRoles.Add(new UserOrganizationRole { UserId = user.Id, OrganizationId = organizationId, RoleId = roleId });
            db.OrganizationMembers.Add(new OrganizationMember { UserId = user.Id, OrganizationId = organizationId, Role = level });
        }
        await db.SaveChangesAsync(ct);
        return (user.Id, user.Username);
    }

    private static async Task<Tenant> SeedTenantAsync(AetheusWebApplicationFactory factory, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var marker = $"adm-{label}-{Guid.NewGuid():N}";
        var organization = new Organization { Name = marker, Slug = $"m{label}{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);
        var project = new Project { Name = $"{marker}-project", OrganizationId = organization.Id };
        var server = new Server
        {
            Name = $"{marker}-server",
            Hostname = $"{marker}.test",
            OrganizationId = organization.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        var environment = new Aetheus.Back.Data.Entities.Environment { Name = $"{marker}-env", ProjectId = project.Id };
        var connection = new ServiceConnection
        {
            Name = $"{marker}-sc",
            ProjectId = project.Id,
            EncryptedPayload = "unused",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var pipeline = new Pipeline { Name = $"{marker}-pipeline", ProjectId = project.Id, YamlDefinition = "name: t\nstages: []" };
        db.Environments.Add(environment);
        db.ServiceConnections.Add(connection);
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        var run = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return new Tenant(organization.Id, project.Id, server.Id, environment.Id, connection.Id, pipeline.Id, run.Id);
    }
}
