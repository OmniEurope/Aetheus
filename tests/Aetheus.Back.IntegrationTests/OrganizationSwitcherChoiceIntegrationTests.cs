// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-33, R-34: the organization chosen in the account menu or in a page is only a request. The lists the choice is
/// drawn from are the server's own (a member gets its memberships, a site Admin every organization), and the
/// endpoints that take an organization refuse a caller who is neither a member of it nor a site Admin, whatever
/// the client sends. Real HTTP, JWT included, real PostgreSQL.
/// </summary>
public sealed class OrganizationSwitcherChoiceIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PlanUrl(int organizationId) =>
        $"/api/pipelines/schedule-plan?organizationId={organizationId}&from=2027-01-04T00:00:00Z&to=2027-01-05T00:00:00Z";

    [Fact]
    public async Task AMember_IsOfferedItsOwnOrganizationOnly_AndCannotUseAnotherByChoosingIt()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "sw-a");
        var b = await SeedOrganizationAsync(factory, "sw-b");
        var member = await IntegrationUsers.CreateAsync(factory.Services, "sw-member",
            [
                new IntegrationUsers.Grant(ResourceType.Pipeline, null, Permission.Read),
                new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Write)
            ], a);
        using var client = await IntegrationUsers.LoginClientAsync(factory, member.Username);

        // What the selector is drawn from: the memberships, never the other organization nor a list of all of them.
        var mine = await client.GetFromJsonAsync<List<MyOrganizationDto>>("/api/organizations/me", IntegrationJsonOptions.Default, Ct);
        Assert.Equal([a], mine!.Select(organization => organization.Id));
        Assert.Empty((await client.GetFromJsonAsync<List<AdministeredOrganizationDto>>("/api/organizations/administered", IntegrationJsonOptions.Default, Ct))!);

        // Choosing the other organization anyway (a forged request) is refused where the choice is used.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PlanUrl(a), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PlanUrl(b), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateProjectAsync(client, a)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateProjectAsync(client, b)).StatusCode);
    }

    [Fact]
    public async Task ASiteAdmin_IsOfferedEveryOrganization_AndMayUseEach()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await SeedOrganizationAsync(factory, "sw-adm-a");
        var b = await SeedOrganizationAsync(factory, "sw-adm-b");
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        // The Admin belongs to neither organization: the membership list does not hold them, the administered list does.
        var mine = await admin.GetFromJsonAsync<List<MyOrganizationDto>>("/api/organizations/me", IntegrationJsonOptions.Default, Ct);
        Assert.DoesNotContain(mine!, organization => organization.Id == a || organization.Id == b);
        var administered = await admin.GetFromJsonAsync<List<AdministeredOrganizationDto>>("/api/organizations/administered", IntegrationJsonOptions.Default, Ct);
        Assert.Contains(administered!, organization => organization.Id == a);
        Assert.Contains(administered!, organization => organization.Id == b);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(PlanUrl(a), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(PlanUrl(b), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateProjectAsync(admin, b)).StatusCode);
    }

    private static Task<HttpResponseMessage> CreateProjectAsync(HttpClient client, int organizationId) =>
        client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"switcher-{Guid.NewGuid():N}",
            Description = "R-33 choice",
            OrganizationId = organizationId
        }, IntegrationJsonOptions.Default, Ct);

    private static async Task<int> SeedOrganizationAsync(AetheusWebApplicationFactory factory, string label)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organization = new Organization { Name = $"{label}-{Guid.NewGuid():N}", Slug = $"s{Guid.NewGuid():N}"[..20] };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        return organization.Id;
    }
}
