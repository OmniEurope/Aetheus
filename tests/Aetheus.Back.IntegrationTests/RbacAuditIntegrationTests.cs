// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// RBAC audit of ADR-059 on the real host and PostgreSQL: every finding of the audit has one test
/// here that failed before its fix. Organization A is the victim, B the caller's own organization;
/// a user holding rights in both is the "two organizations" caller the cross-organization link
/// findings need (rights in both, yet a write must never link one organization's resource to the
/// other's).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RbacAuditIntegrationTests(PostgresFixture fixture)
{
    private static readonly HttpStatusCode[] Refused = [HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.BadRequest];

    // --- Finding 1 (critical): a vault or library of B never points at an environment / project server of A ---

    /// <summary>A request naming B's project and A's environment is already refused 400 by
    /// <c>[AtMostOneOwner]</c> (the audit's exact scenario); this pins it, and pins the variant the fix opens
    /// up: an owner given by the environment or project server alone must be one the caller writes (403).</summary>
    [Theory]
    [InlineData("/api/vaults")]
    [InlineData("/api/variable-libraries")]
    public async Task OwnedResource_ContributorOfB_CannotAttachAnEnvironmentOrProjectServerOfA(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var userB = await RbacAuditSeed.CreateUserAsync(factory.Services, "owned-b", (RoleNames.Contributor, b.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);
        var ownId = route == "/api/vaults" ? b.VaultId : b.LibraryId;
        var detail = await client.GetFromJsonAsync<OwnedResourceDetailDto>($"{route}/{ownId}", IntegrationJsonOptions.Default, ct);

        var bodies = new object[]
        {
            new { name = a.SharedName, projectId = b.ProjectId, environmentId = a.EnvironmentId },
            new { name = a.SharedName, projectId = b.ProjectId, projectServerId = a.ProjectServerId },
            new { name = a.SharedName, environmentId = a.EnvironmentId },
            new { name = a.SharedName, projectServerId = a.ProjectServerId }
        };
        foreach (var body in bodies)
            Assert.Contains((await client.PostAsJsonAsync(route, body, ct)).StatusCode, Refused);
        var moved = await client.PutAsJsonAsync($"{route}/{ownId}",
            new { name = a.SharedName, environmentId = a.EnvironmentId, rowVersion = detail!.RowVersion }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, moved.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Vaults.AnyAsync(row => row.EnvironmentId == a.EnvironmentId || row.ProjectServerId == a.ProjectServerId, ct));
        Assert.False(await db.VariableLibraries.AnyAsync(row => row.EnvironmentId == a.EnvironmentId || row.ProjectServerId == a.ProjectServerId, ct));
    }

    private sealed record OwnedResourceDetailDto(int Id, Guid RowVersion);

    /// <summary>Defence in depth: a row already stored with a project of B and an environment of A (legacy,
    /// or written before the fix) is never resolved for A's runs, nor ranked above A's own vault.</summary>
    [Fact]
    public async Task CrossAccessResolution_IgnoresAVaultOrLibraryOfAnotherProjectPointingAtTheEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryption = seedScope.ServiceProvider.GetRequiredService<Aetheus.Back.Services.IEncryptionService>();
            var forgedVault = new Vault { Name = a.SharedName, ProjectId = b.ProjectId, EnvironmentId = a.EnvironmentId };
            forgedVault.Secrets.Add(new VaultSecret { Key = RbacAuditSeed.SharedKey, EncryptedValue = encryption.EncryptValue("injected") });
            var forgedLibrary = new VariableLibrary { Name = a.SharedName, ProjectId = b.ProjectId, ProjectServerId = a.ProjectServerId };
            forgedLibrary.Entries.Add(new VariableLibraryEntry { Key = RbacAuditSeed.SharedKey, Value = "injected" });
            db.AddRange(forgedVault, forgedLibrary);
            await db.SaveChangesAsync(ct);
        }

        using var scope = factory.Services.CreateScope();
        var vaults = scope.ServiceProvider.GetRequiredService<IVaultService>();
        var libraries = scope.ServiceProvider.GetRequiredService<IVariableLibraryService>();
        var secrets = await vaults.ResolveVaultSecretsWithCrossAccessAsync([a.SharedName], a.ProjectId, ct);
        var variables = await libraries.ResolveLibrariesWithCrossAccessAsync([a.SharedName], a.ProjectId, ct);

        Assert.Equal("a-secret", secrets[RbacAuditSeed.SharedKey]);
        Assert.Equal("a-value", variables[RbacAuditSeed.SharedKey]);
    }

    // --- Finding 8 (and the environment-owned half of finding 1): resources owned through an environment ---

    [Fact]
    public async Task EnvironmentOwnedVaultAndPipeline_BelongToTheEnvironmentsOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var userB = await RbacAuditSeed.CreateUserAsync(factory.Services, "envown-b", (RoleNames.Contributor, b.OrganizationId));
        var userA = await RbacAuditSeed.CreateUserAsync(factory.Services, "envown-a", (RoleNames.Contributor, a.OrganizationId));
        using var clientB = await IntegrationUsers.LoginClientAsync(factory, userB.Username);
        using var clientA = await IntegrationUsers.LoginClientAsync(factory, userA.Username);
        int pipelineId;
        Vault vault;
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pipeline = new Pipeline { Name = $"envown-{Guid.NewGuid():N}", EnvironmentId = b.EnvironmentId, YamlDefinition = RbacAuditSeed.PipelineYaml };
            vault = new Vault { Name = $"envown-{Guid.NewGuid():N}", EnvironmentId = b.EnvironmentId };
            db.Pipelines.Add(pipeline);
            db.Vaults.Add(vault);
            await db.SaveChangesAsync(ct);
            pipelineId = pipeline.Id;
        }

        // Creating one with no ProjectId stays a site Admin's (ADR-059, OrganizationAdministrationIntegrationTests);
        // a site Admin's environment-owned vault is still B's to read.
        Assert.Equal(HttpStatusCode.Forbidden, (await clientB.PostAsJsonAsync("/api/vaults", new CreateVaultRequest
        {
            Name = $"envown-{Guid.NewGuid():N}",
            EnvironmentId = b.EnvironmentId
        }, ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync($"/api/vaults/{vault.Id}", ct)).StatusCode);
        Assert.Contains(vault.Id, await IntegrationUsers.ReadListIdsAsync(await clientB.GetAsync("/api/vaults?pageSize=200", ct)));
        Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync($"/api/pipelines/{pipelineId}", ct)).StatusCode);
        // The other organization still reaches neither.
        Assert.Contains((await clientA.GetAsync($"/api/vaults/{vault.Id}", ct)).StatusCode, Refused);
        Assert.Contains((await clientA.GetAsync($"/api/pipelines/{pipelineId}", ct)).StatusCode, Refused);

        // An environment-owned vault of B is never resolved by name for a run of A's project.
        using var scope = factory.Services.CreateScope();
        var vaults = scope.ServiceProvider.GetRequiredService<IVaultService>();
        var (_, found) = await vaults.ResolveVaultSecretsWithNamesAsync([vault.Name], a.ProjectId, ct);
        Assert.Empty(found);
    }

    // --- Finding 2 (high): environments never copy nor link another organization's servers ---

    [Fact]
    public async Task EnvironmentDuplicateAndCopy_RefuseServersAndProjectServersOfAnotherOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var userB = await RbacAuditSeed.CreateUserAsync(factory.Services, "dup-b", (RoleNames.Contributor, b.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);
        int tainted;
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            // A B environment that (legacy row, or written before the fix) carries A's server and A's project server.
            var environment = new Environment { Name = $"tainted-{Guid.NewGuid():N}", ProjectId = b.ProjectId };
            environment.Servers.Add(new EnvironmentServer { ServerId = a.ServerId });
            environment.LinkedProjectServers.Add(new EnvironmentProjectServer { ProjectServerId = a.ProjectServerId });
            db.Add(environment);
            await db.SaveChangesAsync(ct);
            tainted = environment.Id;
        }

        var duplicated = await client.PostAsJsonAsync($"/api/environments/{tainted}/duplicate", new DuplicateEnvironmentRequest(), ct);
        var copied = await client.PostAsJsonAsync("/api/environments", new CreateEnvironmentRequest
        {
            Name = $"copy-{Guid.NewGuid():N}",
            ProjectId = b.ProjectId,
            SourceEnvironmentId = tainted
        }, ct);

        Assert.Contains(duplicated.StatusCode, Refused);
        Assert.Contains(copied.StatusCode, Refused);
        using var scope = factory.Services.CreateScope();
        var verify = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verify.EnvironmentServers.CountAsync(link => link.ServerId == a.ServerId && link.Environment.ProjectId == b.ProjectId, ct));
    }

    [Fact]
    public async Task LinkProjectServer_RefusesAProjectServerOfAnotherOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        // Reads A, writes B: the link still crosses organizations and is refused.
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "link",
            (RoleNames.Contributor, b.OrganizationId), (RoleNames.Reader, a.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var linked = await client.PostAsync($"/api/environments/{b.EnvironmentId}/project-servers/{a.ProjectServerId}", null, ct);

        Assert.Contains(linked.StatusCode, Refused);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Set<EnvironmentProjectServer>().AnyAsync(link => link.EnvironmentId == b.EnvironmentId && link.ProjectServerId == a.ProjectServerId, ct));
    }

    [Fact]
    public async Task ServersOfAProject_NeverIncludeAServerOfAnotherOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.EnvironmentServers.Add(new EnvironmentServer { EnvironmentId = b.EnvironmentId, ServerId = a.ServerId });
        db.ProjectServers.Add(new ProjectServer
        {
            ProjectId = b.ProjectId,
            ServerId = a.ServerId,
            Type = ProjectServerType.AgentServer,
            DisplayName = "foreign",
            Host = "foreign.test"
        });
        await db.SaveChangesAsync(ct);

        var servers = await db.ServerIdsOfProject(b.ProjectId).ToListAsync(ct);
        var projects = await db.ProjectIdsOfServer(a.ServerId).ToListAsync(ct);

        Assert.Contains(b.ServerId, servers);
        Assert.DoesNotContain(a.ServerId, servers);
        Assert.DoesNotContain(b.ProjectId, projects);
    }

    // --- Finding 3 (high): editing the definition makes the editor the owner automated runs act as ---

    [Fact]
    public async Task PipelineDefinitionEdit_MovesAutomatedRunOwnershipToTheEditor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var userB = await RbacAuditSeed.CreateUserAsync(factory.Services, "owner-b", (RoleNames.Contributor, b.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, userB.Username);

        // A rename that keeps the definition leaves the owner in place.
        var renamed = await client.PutAsJsonAsync($"/api/pipelines/{b.PipelineId}", new UpdatePipelineRequest
        {
            Name = $"{b.Marker}-renamed",
            ProjectId = b.ProjectId,
            YamlDefinition = RbacAuditSeed.PipelineYaml
        }, ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("admin", await OwnerOfAsync(factory, b.PipelineId));

        var edited = await client.PutAsJsonAsync($"/api/pipelines/{b.PipelineId}", new UpdatePipelineRequest
        {
            Name = $"{b.Marker}-renamed",
            ProjectId = b.ProjectId,
            YamlDefinition = RbacAuditSeed.PipelineYaml.Replace("echo one", "echo two", StringComparison.Ordinal)
        }, ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(userB.Username, await OwnerOfAsync(factory, b.PipelineId));
    }

    // --- Finding 4 (medium): port allocation needs Server Write in the library's organization ---

    [Fact]
    public async Task PortAllocation_RefusesAServerOfAnotherOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "ports",
            (RoleNames.Contributor, b.OrganizationId), (RoleNames.Reader, a.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var foreign = await client.PostAsJsonAsync($"/api/variable-libraries/{b.LibraryId}/ports/allocate",
            new AllocateLibraryPortsRequest { ServerId = a.ServerId, Keys = ["PORT_FOREIGN"] }, ct);
        var own = await client.PostAsJsonAsync($"/api/variable-libraries/{b.LibraryId}/ports/allocate",
            new AllocateLibraryPortsRequest { ServerId = b.ServerId, Keys = ["PORT_OWN"] }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.ServerPortReservations.AnyAsync(reservation => reservation.ServerId == a.ServerId, ct));
    }

    // --- Finding 5 (medium): vault names never reuse a wider caller's cached list ---

    [Fact]
    public async Task VaultNames_AUserWithoutVaultsNeverReadsTheAdminsCachedList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        int emptyOrganization;
        var globalVault = $"global-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var organization = new Organization { Name = $"empty-{Guid.NewGuid():N}", Slug = $"e{Guid.NewGuid():N}"[..20] };
            db.Organizations.Add(organization);
            // A vault of no project (site Admin only): the name list without a project offers it to the Admin.
            db.Vaults.Add(new Vault { Name = globalVault });
            await db.SaveChangesAsync(ct);
            emptyOrganization = organization.Id;
        }
        var reader = await RbacAuditSeed.CreateUserAsync(factory.Services, "names", (RoleNames.Reader, emptyOrganization));
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        using var client = await IntegrationUsers.LoginClientAsync(factory, reader.Username);

        var adminNames = await admin.GetFromJsonAsync<List<string>>("/api/vaults/names", IntegrationJsonOptions.Default, ct);
        Assert.Contains(globalVault, adminNames!);

        var names = await client.GetFromJsonAsync<List<string>>("/api/vaults/names", IntegrationJsonOptions.Default, ct);
        Assert.Empty(names!);
        Assert.Contains((await client.GetAsync($"/api/vaults/names?projectId={a.ProjectId}", ct)).StatusCode, Refused);
    }

    // --- Finding 6 (medium): no write links a project of A to a server or environment of B ---

    [Fact]
    public async Task CallerOfBothOrganizations_CannotLinkAProjectOfAToAResourceOfB()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "both",
            (RoleNames.OrganizationAdmin, a.OrganizationId), (RoleNames.OrganizationAdmin, b.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var projectServer = await client.PostAsJsonAsync($"/api/projects/{a.ProjectId}/servers", new CreateProjectServerRequest
        {
            Type = ProjectServerType.AgentServer,
            ServerId = b.ServerId,
            DisplayName = "cross",
            Host = "cross.test"
        }, ct);
        var appOnServer = await client.PostAsJsonAsync($"/api/AppMonitoring/projects/{a.ProjectId}/apps", new CreateMonitoredAppRequest
        {
            ProjectId = a.ProjectId,
            Name = $"cross-{Guid.NewGuid():N}",
            ServerId = b.ServerId,
            ProbeUrl = "http://cross.test/health"
        }, ct);
        var appOnEnvironment = await client.PostAsJsonAsync($"/api/AppMonitoring/projects/{a.ProjectId}/apps", new CreateMonitoredAppRequest
        {
            ProjectId = a.ProjectId,
            Name = $"cross-{Guid.NewGuid():N}",
            EnvironmentId = b.EnvironmentId
        }, ct);
        var backup = await client.PostAsJsonAsync("/api/backups", new CreateBackupPolicyRequest
        {
            Name = $"cross-{Guid.NewGuid():N}",
            ProjectId = a.ProjectId,
            ServerId = b.ServerId,
            DbEngine = BackupDbEngine.Postgres,
            DbHost = "localhost",
            DbName = "app",
            DbUser = "app",
            ScheduleCron = "0 3 * * *"
        }, ct);
        var pipeline = await client.PostAsJsonAsync("/api/pipelines", new CreatePipelineRequest
        {
            Name = $"cross-{Guid.NewGuid():N}",
            ProjectId = a.ProjectId,
            EnvironmentId = b.EnvironmentId,
            YamlDefinition = RbacAuditSeed.PipelineYaml
        }, ct);

        Assert.Contains(projectServer.StatusCode, Refused);
        Assert.Contains(appOnServer.StatusCode, Refused);
        Assert.Contains(appOnEnvironment.StatusCode, Refused);
        Assert.Contains(backup.StatusCode, Refused);
        Assert.Contains(pipeline.StatusCode, Refused);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.ProjectServers.AnyAsync(row => row.ProjectId == a.ProjectId && row.ServerId == b.ServerId, ct));
        Assert.False(await db.MonitoredApps.AnyAsync(app => app.ProjectId == a.ProjectId && app.Name.StartsWith("cross-"), ct));
        Assert.False(await db.Pipelines.AnyAsync(row => row.ProjectId == a.ProjectId && row.EnvironmentId == b.EnvironmentId, ct));
    }

    // --- Finding 7 (medium): member e-mails, inactive users, exact login match ---

    [Fact]
    public async Task OrganizationAdmin_ReadsNoMemberEmail_AndAddsNeitherInactiveNorAmbiguousUsers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var orgAdmin = await RbacAuditSeed.CreateUserAsync(factory.Services, "orgadmin", (RoleNames.OrganizationAdmin, a.OrganizationId));
        await RbacAuditSeed.CreateUserAsync(factory.Services, "member", (RoleNames.Reader, a.OrganizationId));
        var suffix = Guid.NewGuid().ToString("N");
        var mail = $"target-{suffix}@rbac.test";
        int inactiveId, byMailId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var inactive = new User { Username = $"inactive-{suffix}", Email = $"inactive-{suffix}@rbac.test", PasswordHash = "x", IsActive = false };
            // Two users answer the same login: one by e-mail, the other whose username is that e-mail.
            var byMail = new User { Username = $"bymail-{suffix}", Email = mail, PasswordHash = "x", IsActive = true };
            var byName = new User { Username = mail.ToUpperInvariant(), Email = $"other-{suffix}@rbac.test", PasswordHash = "x", IsActive = true };
            db.Users.AddRange(inactive, byMail, byName);
            await db.SaveChangesAsync(ct);
            (inactiveId, byMailId) = (inactive.Id, byMail.Id);
        }
        using var client = await IntegrationUsers.LoginClientAsync(factory, orgAdmin.Username);

        var detail = await client.GetFromJsonAsync<OrganizationDetailDto>($"/api/organizations/{a.OrganizationId}", IntegrationJsonOptions.Default, ct);
        Assert.NotEmpty(detail!.Members);
        Assert.All(detail.Members, member => Assert.Null(member.Email));

        var inactiveAdd = await client.PostAsJsonAsync($"/api/organizations/{a.OrganizationId}/members",
            new AddOrganizationMemberRequest { UserLogin = $"inactive-{suffix}", Role = OrganizationRole.Member }, ct);
        var unknownAdd = await client.PostAsJsonAsync($"/api/organizations/{a.OrganizationId}/members",
            new AddOrganizationMemberRequest { UserLogin = $"nobody-{suffix}", Role = OrganizationRole.Member }, ct);
        Assert.Equal(HttpStatusCode.NotFound, inactiveAdd.StatusCode);
        Assert.Equal(unknownAdd.StatusCode, inactiveAdd.StatusCode);
        // Same answer, word for word, so the reply never tells an inactive account from a missing one.
        Assert.Equal(StripCorrelation(await unknownAdd.Content.ReadAsStringAsync(ct)),
            StripCorrelation(await inactiveAdd.Content.ReadAsStringAsync(ct)));

        // The e-mail match wins, case-insensitively, over a username that happens to equal the e-mail.
        var added = await client.PostAsJsonAsync($"/api/organizations/{a.OrganizationId}/members",
            new AddOrganizationMemberRequest { UserLogin = mail.ToUpperInvariant(), Role = OrganizationRole.Member }, ct);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var member = await added.Content.ReadFromJsonAsync<OrganizationMemberDto>(IntegrationJsonOptions.Default, ct);
        Assert.Equal(byMailId, member!.UserId);

        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verify.OrganizationMembers.AnyAsync(item => item.UserId == inactiveId, ct));
    }

    // --- Finding 10 (low): the last owner, enum inputs ---

    [Fact]
    public async Task LastOwner_IsKeptWhenTheUserSideReplacesItsRoles_AndAnUnknownLevelIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var owner = await RbacAuditSeed.CreateUserAsync(factory.Services, "lastowner", (RoleNames.OrganizationAdmin, a.OrganizationId));
        var reader = await RbacAuditSeed.CreateUserAsync(factory.Services, "plain", (RoleNames.Reader, a.OrganizationId));
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        string ownerEmail;
        using (var scope = factory.Services.CreateScope())
            ownerEmail = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
                .Where(item => item.Id == owner.Id).Select(item => item.Email!).SingleAsync(ct);

        var replaced = await admin.PutAsJsonAsync($"/api/users/{owner.Id}", new UpdateUserRequest
        {
            Username = owner.Username,
            Email = ownerEmail,
            IsActive = true,
            Roles = [],
            OrganizationRoles = [new OrganizationRoleAssignmentDto { OrganizationId = a.OrganizationId, Role = RoleNames.Reader }]
        }, ct);
        Assert.Equal(HttpStatusCode.Conflict, replaced.StatusCode);

        int memberId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(OrganizationRole.Owner, (await db.OrganizationMembers.SingleAsync(item => item.UserId == owner.Id, ct)).Role);
            memberId = await db.OrganizationMembers.Where(item => item.UserId == reader.Id).Select(item => item.Id).SingleAsync(ct);
        }

        using var unknownLevel = new StringContent("{\"role\":99}", Encoding.UTF8, "application/json");
        var refused = await admin.PutAsync($"/api/organizations/{a.OrganizationId}/members/{memberId}", unknownLevel, ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDemotions_NeverLeaveAnOrganizationWithoutOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "race");
            var first = await RbacAuditSeed.CreateUserAsync(factory.Services, "own1", (RoleNames.OrganizationAdmin, a.OrganizationId));
            var second = await RbacAuditSeed.CreateUserAsync(factory.Services, "own2", (RoleNames.OrganizationAdmin, a.OrganizationId));
            List<int> memberIds;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                memberIds = await db.OrganizationMembers
                    .Where(item => item.OrganizationId == a.OrganizationId && (item.UserId == first.Id || item.UserId == second.Id))
                    .Select(item => item.Id).ToListAsync(ct);
            }

            var responses = await Task.WhenAll(memberIds.Select(memberId => admin.PutAsJsonAsync(
                $"/api/organizations/{a.OrganizationId}/members/{memberId}",
                new UpdateOrganizationMemberRequest { Role = OrganizationRole.Member }, ct)));

            Assert.All(responses, response => Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode));
            using var verifyScope = factory.Services.CreateScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await verify.OrganizationMembers.CountAsync(
                item => item.OrganizationId == a.OrganizationId && item.Role == OrganizationRole.Owner, ct));
        }
    }

    // --- Finding 9 (low): a type-level right held in another organization creates nothing here ---

    [Fact]
    public async Task ProductionRepair_NeedsTheLibraryRightInTheProjectsOwnOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        // Project Write on A's project only (no library right in A), Contributor in B.
        var user = await IntegrationUsers.CreateAsync(factory.Services, "setup",
            [new IntegrationUsers.Grant(ResourceType.Project, a.ProjectId, Permission.Write)], a.OrganizationId);
        await RbacAuditSeed.GrantAsync(factory.Services, user.Id, (RoleNames.Contributor, b.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var repaired = await client.PostAsJsonAsync("/api/pipelines/setup/production-repair",
            new PipelineProductionRepairRequest { ProjectId = a.ProjectId }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, repaired.StatusCode);
    }

    // --- Finding 12 (low): one project event costs the same SQL whatever the number of subscribers ---

    [Fact]
    public async Task ProjectEvent_SubscriberAuthorizationIsNotOneQueryPerSubscriber()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var few = await RbacAuditSeed.SeedTenantAsync(factory.Services, "few");
        var many = await RbacAuditSeed.SeedTenantAsync(factory.Services, "many");
        await SubscribeReadersAsync(factory, few, 2);
        await SubscribeReadersAsync(factory, many, 10);
        using var counter = new SqlCommandCounter();

        var fewCost = await counter.MeasureAsync(() => RecordAsync(factory, few.ProjectId));
        var manyCost = await counter.MeasureAsync(() => RecordAsync(factory, many.ProjectId));

        Assert.Equal(2, fewCost.Result);
        Assert.Equal(10, manyCost.Result);
        // The subscribers' access is checked in one query whatever their number. The e-mail rendering still
        // reads per recipient (its template and style), which is not authorization and is reported apart.
        Assert.Equal(fewCost.PermissionCommands, manyCost.PermissionCommands);
        Assert.InRange(manyCost.PermissionCommands, 1, 2);
    }

    private static async Task<int> RecordAsync(AetheusWebApplicationFactory factory, int projectId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUserNotificationService>().RecordProjectEventAsync(
            NotificationEventTypes.PipelineFailed, $"{{\"ProjectId\":{projectId}}}", TestContext.Current.CancellationToken);
    }

    private static async Task SubscribeReadersAsync(AetheusWebApplicationFactory factory, RbacAuditSeed.Tenant tenant, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var readers = new List<int>();
        for (var index = 0; index < count; index++)
            readers.Add((await RbacAuditSeed.CreateUserAsync(factory.Services, "sub", (RoleNames.Reader, tenant.OrganizationId))).Id);
        // An outsider subscribed to the project (forged, or from before losing access) is filtered out.
        var outsider = await RbacAuditSeed.CreateUserAsync(factory.Services, "out");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var userId in readers.Append(outsider.Id))
            db.ProjectSubscriptions.Add(new ProjectSubscription { UserId = userId, ProjectId = tenant.ProjectId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> OwnerOfAsync(AetheusWebApplicationFactory factory, int pipelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Pipelines.AsNoTracking().Where(row => row.Id == pipelineId)
            .Select(row => row.CreatedByUsername).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static string StripCorrelation(string body)
    {
        var index = body.IndexOf("\"correlationId\"", StringComparison.Ordinal);
        return index < 0 ? body : body[..index];
    }
}
