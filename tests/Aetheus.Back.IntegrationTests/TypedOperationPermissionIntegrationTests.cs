// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Grant = Aetheus.Back.IntegrationTests.IntegrationUsers.Grant;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// BACK-003 on the real host and PostgreSQL: <c>POST /api/tasks/operation</c> requires, for each kind, what
/// the kind's dedicated endpoint requires. A Contributor (Server Write, no firewall key) is refused a
/// firewall rule and a container exec there, exactly as on <c>/api/servers/{id}/firewall/allow</c> and
/// <c>/docker/exec</c>, and still queues a plain container restart; the key, or Server Admin, opens the
/// stricter kinds.
/// </summary>
public sealed class TypedOperationPermissionIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateOperationRequest Operation(int serverId, OperationKind kind, string target) =>
        new() { ServerId = serverId, Name = $"itest {kind}", Operation = kind, Target = target };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, CreateOperationRequest request) =>
        client.PostAsJsonAsync("/api/tasks/operation", request, IntegrationJsonOptions.Default, Ct);

    [Fact]
    public async Task AContributor_IsRefusedTheStricterKinds_AndStillQueuesAPlainOne()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "tops");
        var contributor = await RbacAuditSeed.CreateUserAsync(factory.Services, "tops-contrib", (RoleNames.Contributor, tenant.OrganizationId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, contributor.Username);

        var firewall = await PostAsync(client, Operation(tenant.ServerId, OperationKind.FirewallAllow, "8443"));
        Assert.Equal(HttpStatusCode.Forbidden, firewall.StatusCode);
        var exec = await PostAsync(client, Operation(tenant.ServerId, OperationKind.DockerExecScript, "abcdef123456"));
        Assert.Equal(HttpStatusCode.Forbidden, exec.StatusCode);
        var upgrade = await PostAsync(client, Operation(tenant.ServerId, OperationKind.SystemPackageUpgrade, "-"));
        Assert.Equal(HttpStatusCode.Forbidden, upgrade.StatusCode);

        var restart = await PostAsync(client, Operation(tenant.ServerId, OperationKind.DockerRestartContainer, "web"));
        Assert.True(restart.StatusCode == HttpStatusCode.Created, $"restart: {restart.StatusCode} {await restart.Content.ReadAsStringAsync(Ct)}");
    }

    [Fact]
    public async Task TheFirewallKey_OrServerAdmin_OpensTheStricterKinds()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var tenant = await RbacAuditSeed.SeedTenantAsync(factory.Services, "topk");

        var keyed = await IntegrationUsers.CreateAsync(factory.Services, "topk-fw",
            [new Grant(ResourceType.Server, null, Permission.Write)], tenant.OrganizationId, [PermissionKeys.ServerFirewallManage]);
        using (var client = await IntegrationUsers.LoginClientAsync(factory, keyed.Username))
        {
            var firewall = await PostAsync(client, Operation(tenant.ServerId, OperationKind.FirewallAllow, "8443"));
            Assert.True(firewall.StatusCode == HttpStatusCode.Created, $"firewall with key: {firewall.StatusCode}");
            // The firewall key is not Server Admin.
            var exec = await PostAsync(client, Operation(tenant.ServerId, OperationKind.DockerExecScript, "abcdef123456"));
            Assert.Equal(HttpStatusCode.Forbidden, exec.StatusCode);
        }

        var serverAdmin = await IntegrationUsers.CreateAsync(factory.Services, "topk-admin",
            [new Grant(ResourceType.Server, null, Permission.Admin)], tenant.OrganizationId);
        using (var client = await IntegrationUsers.LoginClientAsync(factory, serverAdmin.Username))
        {
            var exec = await PostAsync(client, Operation(tenant.ServerId, OperationKind.DockerExecScript, "abcdef123456"));
            Assert.True(exec.StatusCode == HttpStatusCode.Created, $"exec as Server Admin: {exec.StatusCode}");
            // R-11: Server Admin alone does not hold the firewall key, here as on the dedicated endpoint.
            var firewall = await PostAsync(client, Operation(tenant.ServerId, OperationKind.FirewallAllow, "8443"));
            Assert.Equal(HttpStatusCode.Forbidden, firewall.StatusCode);
        }
    }
}
