// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Grant = Aetheus.Back.IntegrationTests.IntegrationUsers.Grant;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-11 on the real host and PostgreSQL: each sensitive action is its own named permission. A role
/// holding the level that used to allow the action (even the Admin level) is refused 403 without the
/// key; the same role with the key gets through. The custom "server contributor" role the owner asked
/// for works on servers and not on pipelines, the container environment is an Organization Admin's by
/// default, and port reservation is refused to a Reader and across organizations.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PermissionKeysHttpIntegrationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FirewallRuleRequest Rule = new() { Port = 8443, Protocol = "tcp", Source = "any" };

    /// <summary>Every converted server action: route, body, key.</summary>
    public static TheoryData<string> ServerActions() =>
        ["services/action", "services/install", "services/uninstall", "system/upgrade",
         "firewall/allow", "firewall/deny", "firewall/delete", "firewall/toggle", "docker/containers/abcdef123456/env"];

    private static (string Route, object Body, string Key) ServerAction(string action, int serverId) => action switch
    {
        "services/action" => ($"/api/servers/{serverId}/services/action",
            new ServiceActionRequest { Action = ServiceAction.Restart, ServiceName = "nginx" }, PermissionKeys.ServerServicesControl),
        "services/install" => ($"/api/servers/{serverId}/services/install",
            new ServiceInstallRequest { ServiceName = "nginx" }, PermissionKeys.ServerServicesControl),
        "services/uninstall" => ($"/api/servers/{serverId}/services/uninstall",
            new ServiceInstallRequest { ServiceName = "nginx" }, PermissionKeys.ServerServicesControl),
        "system/upgrade" => ($"/api/servers/{serverId}/system/upgrade",
            new SystemUpgradeRequest { DryRun = true }, PermissionKeys.ServerSystemUpgrade),
        "firewall/toggle" => ($"/api/servers/{serverId}/firewall/toggle",
            new FirewallToggleRequest { Enabled = true }, PermissionKeys.ServerFirewallManage),
        "docker/containers/abcdef123456/env" => ($"/api/servers/{serverId}/docker/containers/abcdef123456/env",
            new { }, PermissionKeys.ServerDockerContainerEnvView),
        _ => ($"/api/servers/{serverId}/{action}", Rule, PermissionKeys.ServerFirewallManage)
    };

    [Theory]
    [MemberData(nameof(ServerActions))]
    public async Task ServerAction_TheLevelAloneIsRefused_TheKeyAllowsIt(string action)
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await ManagedTenantAsync(factory, "srv");
        var (route, body, key) = ServerAction(action, tenant.ServerId);

        // Server Admin used to allow every one of these actions; without the named permission it no longer does.
        var levelOnly = await UserAsync(factory, tenant, [new Grant(ResourceType.Server, null, Permission.Admin)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await levelOnly.PostAsJsonAsync(route, body, IntegrationJsonOptions.Default, Ct)).StatusCode);

        // The key is what decides: Server Read plus the key gets through.
        var keyed = await UserAsync(factory, tenant, [new Grant(ResourceType.Server, null, Permission.Read)], key);
        var allowed = await keyed.PostAsJsonAsync(route, body, IntegrationJsonOptions.Default, Ct);
        Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"{action}: {allowed.StatusCode} {await allowed.Content.ReadAsStringAsync(Ct)}");
    }

    /// <summary>Owner decision (2026-10-28): the firewall and the system upgrade are an Organization Admin's by
    /// default; the built-in Contributor, which held them through Server Write, no longer does.</summary>
    [Theory]
    [InlineData("firewall/allow")]
    [InlineData("firewall/deny")]
    [InlineData("firewall/delete")]
    [InlineData("firewall/toggle")]
    [InlineData("system/upgrade")]
    public async Task FirewallAndSystemUpgrade_AreAnOrganizationAdminsByDefault_NotAContributors(string action)
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await ManagedTenantAsync(factory, "fw");
        var (route, body, _) = ServerAction(action, tenant.ServerId);

        foreach (var (role, expected) in new[]
                 {
                     (RoleNames.Contributor, HttpStatusCode.Forbidden),
                     (RoleNames.OrganizationAdmin, HttpStatusCode.OK)
                 })
        {
            var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "fw", (role, tenant.OrganizationId));
            using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);
            var response = await client.PostAsJsonAsync(route, body, IntegrationJsonOptions.Default, Ct);
            Assert.True(expected == response.StatusCode, $"{role} on {action}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task ServerContributor_CustomRole_WorksOnServersButNotOnPipelines()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await ManagedTenantAsync(factory, "sc");
        var client = await UserAsync(factory, tenant,
            [new Grant(ResourceType.Server, null, Permission.Write), new Grant(ResourceType.Pipeline, null, Permission.Read)],
            PermissionKeys.ServerServicesControl, PermissionKeys.ServerFirewallManage, PermissionKeys.ServerPortsReserve);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"/api/servers/{tenant.ServerId}/firewall/allow", Rule, IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/servers/{tenant.ServerId}/ports",
            new CreatePortReservationRequest { Port = 42010, OwnerLabel = "server-contributor" }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        // It reads pipelines, and does not run them, nor upgrade the system it was not given.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/pipelines/{tenant.PipelineId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/pipelines/{tenant.PipelineId}/run", new PipelineRunRequest(), IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/servers/{tenant.ServerId}/system/upgrade",
            new SystemUpgradeRequest { DryRun = true }, IntegrationJsonOptions.Default, Ct)).StatusCode);
    }

    [Fact]
    public async Task DockerContainerEnvironment_IsAnOrganizationAdminsByDefault()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "env");
        var route = $"/api/servers/{tenant.ServerId}/docker/containers/abcdef123456/env";

        foreach (var (role, expected) in new[]
                 {
                     (RoleNames.Reader, HttpStatusCode.Forbidden),
                     (RoleNames.Contributor, HttpStatusCode.Forbidden),
                     (RoleNames.OrganizationAdmin, HttpStatusCode.OK)
                 })
        {
            var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "env", (role, tenant.OrganizationId));
            using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);
            Assert.True(expected == (await client.PostAsync(route, null, Ct)).StatusCode, $"{role} on the container environment");
        }
    }

    [Fact]
    public async Task PortReservation_IsRefusedToAReaderAndAcrossOrganizations()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "pa");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "pb");
        var reader = await RbacAuditSeed.CreateUserAsync(factory.Services, "port-reader", (RoleNames.Reader, a.OrganizationId));
        var contributorA = await RbacAuditSeed.CreateUserAsync(factory.Services, "port-contrib", (RoleNames.Contributor, a.OrganizationId));
        var contributorB = await RbacAuditSeed.CreateUserAsync(factory.Services, "port-other", (RoleNames.Contributor, b.OrganizationId));
        var request = new CreatePortReservationRequest { Port = 42020, OwnerLabel = "r11" };

        using (var client = await IntegrationUsers.LoginClientAsync(factory, reader.Username))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/servers/{a.ServerId}/ports", request, IntegrationJsonOptions.Default, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/variable-libraries/{a.LibraryId}/ports/allocate",
                new AllocateLibraryPortsRequest { ServerId = a.ServerId, Keys = ["PORT_R11"] }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        }
        // Library Write and Server Read (what allocating used to need): without the reservation key, refused.
        var libraryWriter = await UserAsync(factory, a,
            [new Grant(ResourceType.VariableLibrary, null, Permission.Write), new Grant(ResourceType.Server, null, Permission.Read)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await libraryWriter.PostAsJsonAsync($"/api/variable-libraries/{a.LibraryId}/ports/allocate",
            new AllocateLibraryPortsRequest { ServerId = a.ServerId, Keys = ["PORT_R11"] }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        using (var client = await IntegrationUsers.LoginClientAsync(factory, contributorB.Username))
        {
            // Contributor of B: a server of A, or A's project named on B's own server, are both refused.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/servers/{a.ServerId}/ports", request, IntegrationJsonOptions.Default, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/servers/{b.ServerId}/ports",
                request with { ProjectId = a.ProjectId }, IntegrationJsonOptions.Default, Ct)).StatusCode);
        }
        using (var client = await IntegrationUsers.LoginClientAsync(factory, contributorA.Username))
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/servers/{a.ServerId}/ports",
                request with { ProjectId = a.ProjectId }, IntegrationJsonOptions.Default, Ct)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.ServerPortReservations.CountAsync(row => row.Port == 42020, Ct));
    }

    [Fact]
    public async Task VaultSecretReveal_TheLevelAloneIsRefused_TheKeyRevealsTheValue()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "vlt");
        int secretId;
        using (var scope = factory.Services.CreateScope())
            secretId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().VaultSecrets
                .Where(secret => secret.VaultId == tenant.VaultId).Select(secret => secret.Id).SingleAsync(Ct);
        var route = $"/api/vaults/{tenant.VaultId}/secrets/{secretId}/reveal";

        var levelOnly = await UserAsync(factory, tenant, [new Grant(ResourceType.Vault, null, Permission.Admin)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await levelOnly.PostAsync(route, null, Ct)).StatusCode);

        var keyed = await UserAsync(factory, tenant, [new Grant(ResourceType.Vault, null, Permission.Read)], PermissionKeys.VaultSecretReveal);
        var revealed = await keyed.PostAsync(route, null, Ct);
        Assert.Equal(HttpStatusCode.OK, revealed.StatusCode);
        Assert.Contains("vlt-secret", await revealed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipelineRun_TheLevelAloneIsRefused_TheKeyGetsThrough()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "run");
        var route = $"/api/pipelines/{tenant.PipelineId}/parameters";

        var levelOnly = await UserAsync(factory, tenant,
            [new Grant(ResourceType.Pipeline, null, Permission.Admin), new Grant(ResourceType.Server, null, Permission.Admin)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await levelOnly.GetAsync(route, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await levelOnly.PostAsJsonAsync(
            $"/api/pipelines/{tenant.PipelineId}/run", new PipelineRunRequest(), IntegrationJsonOptions.Default, Ct)).StatusCode);

        var keyed = await UserAsync(factory, tenant,
            [new Grant(ResourceType.Pipeline, null, Permission.Read), new Grant(ResourceType.Server, null, Permission.Admin)],
            PermissionKeys.PipelineRun);
        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync(route, Ct)).StatusCode);
    }

    [Fact]
    public async Task ReleaseStatusRestore_TheLevelAloneIsRefused_TheKeyRestores()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "rel");
        int releaseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var release = new Release
            {
                ProjectId = tenant.ProjectId,
                Version = "1.0.0",
                Status = ReleaseStatus.Failed,
                DetectedAt = DateTime.UtcNow,
                PublishedAt = DateTime.UtcNow
            };
            db.Releases.Add(release);
            await db.SaveChangesAsync(Ct);
            releaseId = release.Id;
        }
        var restore = new RestoreReleaseStatusRequest { Status = ReleaseStatus.Deployed, Reason = "still served" };
        var route = $"/api/releases/{releaseId}/status";

        var levelOnly = await UserAsync(factory, tenant,
            [new Grant(ResourceType.Release, null, Permission.Admin), new Grant(ResourceType.Project, null, Permission.Admin)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await levelOnly.PostAsJsonAsync(route, restore, IntegrationJsonOptions.Default, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await levelOnly.PostAsJsonAsync($"/api/releases/{releaseId}/rollback",
            new RollbackReleaseRequest { PipelineId = tenant.PipelineId }, IntegrationJsonOptions.Default, Ct)).StatusCode);

        var keyed = await UserAsync(factory, tenant,
            [new Grant(ResourceType.Release, null, Permission.Read), new Grant(ResourceType.Project, null, Permission.Admin)],
            PermissionKeys.ReleaseStatusRestore);
        Assert.Equal(HttpStatusCode.OK, (await keyed.PostAsJsonAsync(route, restore, IntegrationJsonOptions.Default, Ct)).StatusCode);
    }

    [Fact]
    public async Task MemberManagement_IsTheKeyNotTheLevels()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "mem");
        var newcomer = await IntegrationUsers.CreateAsync(factory.Services, "newcomer", []);
        var add = new AddOrganizationMemberRequest { UserLogin = newcomer.Username, Role = OrganizationRole.Member };
        var route = $"/api/organizations/{tenant.OrganizationId}/members";

        // Every level at Admin, no key: the role manages the resources, not the members.
        var allLevels = await UserAsync(factory, tenant,
            Enum.GetValues<ResourceType>().Select(type => new Grant(type, null, Permission.Admin)).ToList());
        Assert.Equal(HttpStatusCode.Forbidden, (await allLevels.PostAsJsonAsync(route, add, IntegrationJsonOptions.Default, Ct)).StatusCode);

        var orgAdmin = await RbacAuditSeed.CreateUserAsync(factory.Services, "mem-admin", (RoleNames.OrganizationAdmin, tenant.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, orgAdmin.Username);
        Assert.True((await client.PostAsJsonAsync(route, add, IntegrationJsonOptions.Default, Ct)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Mailbox_ReadingIsTheReaderDefault_TheServerLevelAloneIsNot()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "mbx");
        var route = $"/api/servers/{tenant.ServerId}/mail/accounts/1/mailbox/lock";

        var levelOnly = await UserAsync(factory, tenant, [new Grant(ResourceType.Server, null, Permission.Admin)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await levelOnly.PostAsync(route, null, Ct)).StatusCode);

        var reader = await RbacAuditSeed.CreateUserAsync(factory.Services, "mbx-reader", (RoleNames.Reader, tenant.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, reader.Username);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(route, null, Ct)).StatusCode);
        // Changing the mailbox stays a Contributor's.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            $"/api/servers/{tenant.ServerId}/mail/accounts/1/mailbox/messages/1/read?folder=INBOX",
            new MailboxReadStateRequest { IsRead = true }, IntegrationJsonOptions.Default, Ct)).StatusCode);
    }

    [Fact]
    public async Task RolePermissionKeys_AreSetByTheAdmin_AndReportedToTheUser()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await ManagedTenantAsync(factory, "keys");
        var user = await IntegrationUsers.CreateAsync(factory.Services, "keys", [], tenant.OrganizationId);
        int roleId;
        using (var scope = factory.Services.CreateScope())
            roleId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserOrganizationRoles
                .Where(held => held.UserId == user.Id).Select(held => held.RoleId).SingleAsync(Ct);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        var set = await admin.PutAsJsonAsync($"/api/roles/{roleId}/permission-keys",
            new SetRolePermissionKeysRequest { Keys = [PermissionKeys.ServerEdit, PermissionKeys.ServerFirewallManage] }, IntegrationJsonOptions.Default, Ct);
        Assert.True(set.IsSuccessStatusCode, $"PUT permission-keys answered {set.StatusCode}");
        var stored = await admin.GetFromJsonAsync<List<string>>($"/api/roles/{roleId}/permission-keys", IntegrationJsonOptions.Default, Ct);
        // A level key includes the levels below it.
        Assert.Equal([PermissionKeys.ServerView, PermissionKeys.ServerEdit, PermissionKeys.ServerFirewallManage], stored);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/roles/{roleId}/permission-keys",
            new SetRolePermissionKeysRequest { Keys = ["server.everything"] }, IntegrationJsonOptions.Default, Ct)).StatusCode);

        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);
        var summary = await client.GetFromJsonAsync<UserPermissionSummaryDto>("/api/users/me/permissions", IntegrationJsonOptions.Default, Ct);
        Assert.Contains(summary!.PermissionKeys, key => key.Key == PermissionKeys.ServerFirewallManage && key.OrganizationId == tenant.OrganizationId);
        Assert.Contains(summary.PermissionKeys, key => key.Key == PermissionKeys.ServerEdit && key.OrganizationId == tenant.OrganizationId);
        Assert.DoesNotContain(summary.PermissionKeys, key => key.Key == PermissionKeys.ServerSystemUpgrade);
        // And the key set is what the server enforces.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"/api/servers/{tenant.ServerId}/firewall/allow", Rule, IntegrationJsonOptions.Default, Ct)).StatusCode);
    }

    /// <summary>A tenant whose server's agent was installed with the package, patch and firewall modules,
    /// so an authorized action is queued rather than refused for a missing capability.</summary>
    private static async Task<RbacAuditSeed.Tenant> ManagedTenantAsync(AetheusWebApplicationFactory factory, string label)
    {
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, label);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = await db.Servers.SingleAsync(row => row.Id == tenant.ServerId, Ct);
        server.PackageManagementAvailable = true;
        server.PatchManagementAvailable = true;
        server.FirewallManagementAvailable = true;
        await db.SaveChangesAsync(Ct);
        return tenant;
    }

    private static async Task<HttpClient> UserAsync(
        AetheusWebApplicationFactory factory, RbacAuditSeed.Tenant tenant, IReadOnlyCollection<Grant> grants, params string[] keys)
    {
        var user = await IntegrationUsers.CreateAsync(factory.Services, "r11", grants, tenant.OrganizationId, keys);
        return await IntegrationUsers.LoginClientAsync(factory, user.Username);
    }
}
