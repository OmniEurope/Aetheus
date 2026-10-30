// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-78, on the real host and PostgreSQL: an organization that defines no template of its own starts from
/// the delivery templates the platform organization (slug <c>aetheus</c>) ships, the way the template
/// resolver runs them. A user of such an organization lists and reads them, never changes them; its own
/// template of the same name wins; another customer organization's template stays out of reach.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SharedDeliveryTemplateFallbackIntegrationTests(PostgresFixture fixture)
{
    private const string PlatformSlug = "aetheus";
    private const string SharedTemplate = "application-ci";

    [Fact]
    public async Task ClientOrganizationWithoutTemplates_ListsAndReadsTheSharedTemplate_ButCannotChangeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var shared = await PlatformTemplateAsync(factory.Services, SharedTemplate);
        var client = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78c");
        // A contributor writes templates in its own organization: a refusal below is the shared template's.
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "r78c", (RoleNames.Contributor, client.OrganizationId));
        using var http = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var listed = await ListAsync(http);
        var entry = Assert.Single(listed, item => item.Name == SharedTemplate);
        Assert.Equal(shared.Id, entry.Id);
        Assert.True(entry.IsShared);
        // The contributor writes templates in its organization, yet this one is shared: the UI shows it read-only.
        Assert.False(entry.CanEdit);
        Assert.Equal(0, entry.PipelineCount);
        Assert.Null(entry.LatestRunAt);
        var detail = await http.GetFromJsonAsync<PipelineTemplateDto>(
            $"/api/pipelines/templates/{shared.Id}", IntegrationJsonOptions.Default, ct);
        Assert.True(detail?.IsShared);
        Assert.False(detail?.CanEdit);

        using var version = await http.GetAsync(
            $"/api/pipelines/templates/{shared.Id}/versions/{shared.LatestVersion}", ct);
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        var content = await version.Content.ReadFromJsonAsync<PipelineTemplateVersionDto>(IntegrationJsonOptions.Default, ct);
        Assert.False(string.IsNullOrWhiteSpace(content?.YamlContent));
        using var history = await http.GetAsync($"/api/pipelines/templates/{shared.Id}/versions", ct);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        // Exporting gives the same YAML the detail already shows: read only, so allowed too.
        using var export = await http.GetAsync($"/api/pipelines/templates/{shared.Id}/export", ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        using var update = await http.PutAsJsonAsync($"/api/pipelines/templates/{shared.Id}", new UpdatePipelineTemplateRequest
        {
            Name = SharedTemplate,
            Category = "x",
            YamlContent = RbacAuditSeed.PipelineYaml,
            ChangelogEntry = "hijack"
        }, ct);
        using var delete = await http.DeleteAsync($"/api/pipelines/templates/{shared.Id}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        using var verify = factory.Services.CreateScope();
        var stored = await verify.ServiceProvider.GetRequiredService<AppDbContext>().PipelineTemplates.AsNoTracking()
            .SingleAsync(row => row.Id == shared.Id, ct);
        Assert.Equal(shared.LatestVersion, stored.LatestVersion);
    }

    [Fact]
    public async Task ClientOrganizationWithItsOwnTemplateOfThatName_SeesOnlyItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var shared = await PlatformTemplateAsync(factory.Services, SharedTemplate);
        var client = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78o");
        var ownId = await AddTemplateAsync(factory.Services, client.OrganizationId, SharedTemplate);
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "r78o", (RoleNames.Reader, client.OrganizationId));
        using var http = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var listed = await ListAsync(http);

        var entry = Assert.Single(listed, item => string.Equals(item.Name, SharedTemplate, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ownId, entry.Id);
        Assert.False(entry.IsShared);
        // A reader has no Write on its own template either: read-only for the right reason.
        Assert.False(entry.CanEdit);
        Assert.DoesNotContain(listed, item => item.Id == shared.Id);
    }

    [Fact]
    public async Task ContributorsOwnTemplate_IsEditable_InTheListAndTheDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var client = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78e");
        var ownId = await AddTemplateAsync(factory.Services, client.OrganizationId, $"{client.Marker}-own");
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "r78e", (RoleNames.Contributor, client.OrganizationId));
        using var http = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var entry = Assert.Single(await ListAsync(http), item => item.Id == ownId);
        var detail = await http.GetFromJsonAsync<PipelineTemplateDto>(
            $"/api/pipelines/templates/{ownId}", IntegrationJsonOptions.Default, ct);

        Assert.True(entry.CanEdit);
        Assert.False(entry.IsShared);
        Assert.True(detail?.CanEdit);
        Assert.False(detail?.IsShared);
    }

    [Fact]
    public async Task AnotherCustomerOrganizationsTemplate_AndAPrivatePlatformTemplate_StayOutOfReach()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var shared = await PlatformTemplateAsync(factory.Services, SharedTemplate);
        var client = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78x");
        var other = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78y");
        // The other customer even holds a catalog name: it is that organization's own, never shared.
        var otherCatalogNamed = await AddTemplateAsync(factory.Services, other.OrganizationId, SharedTemplate);
        var otherPrivate = await AddTemplateAsync(factory.Services, other.OrganizationId, $"{other.Marker}-private");
        // A platform template outside the shipped catalog stays the platform organization's.
        var platformPrivate = await AddTemplateAsync(factory.Services, shared.OrganizationId, $"{client.Marker}-platform-only");
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "r78x", (RoleNames.Contributor, client.OrganizationId));
        using var http = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var listed = await ListAsync(http);

        Assert.Contains(listed, item => item.Id == shared.Id);
        foreach (var hidden in new[] { otherCatalogNamed, otherPrivate, platformPrivate })
        {
            Assert.DoesNotContain(listed, item => item.Id == hidden);
            using var read = await http.GetAsync($"/api/pipelines/templates/{hidden}", ct);
            using var version = await http.GetAsync($"/api/pipelines/templates/{hidden}/versions/1", ct);
            using var history = await http.GetAsync($"/api/pipelines/templates/{hidden}/versions", ct);
            using var export = await http.GetAsync($"/api/pipelines/templates/{hidden}/export", ct);
            Assert.Equal(HttpStatusCode.Forbidden, export.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, version.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
        }
    }

    [Fact]
    public async Task UserWithoutAnyTemplateRight_DoesNotGetTheSharedTemplates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var shared = await PlatformTemplateAsync(factory.Services, SharedTemplate);
        var client = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78n");
        int userId;
        string username;
        using (var scope = factory.Services.CreateScope())
        {
            // A member of the organization with no role: no template right anywhere.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (userId, username) = await RbacAuditSeed.CreateUserAsync(factory.Services, "r78n");
            db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = client.OrganizationId,
                UserId = userId,
                Role = OrganizationRole.Member
            });
            await db.SaveChangesAsync(ct);
        }
        using var http = await IntegrationUsers.LoginClientAsync(factory, username);

        var listed = await ListAsync(http);
        using var version = await http.GetAsync(
            $"/api/pipelines/templates/{shared.Id}/versions/{shared.LatestVersion}", ct);

        Assert.DoesNotContain(listed, item => item.Id == shared.Id);
        Assert.Equal(HttpStatusCode.Forbidden, version.StatusCode);
    }

    [Fact]
    public async Task FleetItem_OfAClientPipelineExtendingTheSharedTemplate_ResolvesThatTemplate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var shared = await PlatformTemplateAsync(factory.Services, SharedTemplate);
        var client = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78f");
        int pipelineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pipeline = new Pipeline
            {
                Name = $"{client.Marker}-ci",
                ProjectId = client.ProjectId,
                YamlDefinition = $"name: ci\nextends: {SharedTemplate}@{shared.LatestVersion}\nstages: []\n",
                CreatedByUsername = "admin"
            };
            db.Pipelines.Add(pipeline);
            await db.SaveChangesAsync(ct);
            pipelineId = pipeline.Id;
        }
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "r78f", (RoleNames.Contributor, client.OrganizationId));
        using var http = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        using var response = await http.GetAsync($"/api/pipelines/{pipelineId}/fleet-item", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await response.Content.ReadFromJsonAsync<PipelineFleetItemDto>(IntegrationJsonOptions.Default, ct);
        Assert.Equal(shared.Id, item?.TemplateId);
        Assert.Equal(PipelineFleetFreshness.Current, item?.Freshness);
    }

    [Fact]
    public async Task FleetList_OfAClientPipelineExtendingTheSharedTemplate_ResolvesThatTemplateAsCurrent()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var shared = await PlatformTemplateAsync(factory.Services, SharedTemplate);
        var client = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78l");
        var pipelineId = await AddPipelineAsync(factory.Services, client, SharedTemplate, shared.LatestVersion);
        var user = await RbacAuditSeed.CreateUserAsync(factory.Services, "r78l", (RoleNames.Contributor, client.OrganizationId));
        using var http = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var item = Assert.Single(await FleetListAsync(http), row => row.PipelineId == pipelineId);

        Assert.Equal(shared.Id, item.TemplateId);
        Assert.Equal(shared.LatestVersion, item.LatestVersion);
        Assert.Equal(PipelineFleetFreshness.Current, item.Freshness);
    }

    [Fact]
    public async Task FleetList_OfAClientPipelineOnAnOlderSharedVersion_IsOutdated_AndAnOwnTemplateOfThatNameWins()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var shared = await PlatformTemplateAsync(factory.Services, SharedTemplate);
        var client = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78p");
        var own = await RbacAuditSeed.SeedTenantAsync(factory.Services, "r78q");
        var ownId = await AddTemplateAsync(factory.Services, own.OrganizationId, SharedTemplate);
        var olderPipeline = await AddPipelineAsync(factory.Services, client, SharedTemplate, shared.LatestVersion - 1);
        var ownPipeline = await AddPipelineAsync(factory.Services, own, SharedTemplate, 1);
        var user = await RbacAuditSeed.CreateUserAsync(
            factory.Services, "r78p", (RoleNames.Contributor, client.OrganizationId), (RoleNames.Contributor, own.OrganizationId));
        using var http = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var list = await FleetListAsync(http);

        var older = Assert.Single(list, row => row.PipelineId == olderPipeline);
        Assert.Equal(shared.Id, older.TemplateId);
        Assert.Equal(PipelineFleetFreshness.Outdated, older.Freshness);
        var owned = Assert.Single(list, row => row.PipelineId == ownPipeline);
        Assert.Equal(ownId, owned.TemplateId);
        Assert.Equal(PipelineFleetFreshness.Current, owned.Freshness);
    }

    private static async Task<List<PipelineFleetItemDto>> FleetListAsync(HttpClient http)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await http.GetAsync("/api/pipelines/fleet?pageSize=100", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PaginatedResult<PipelineFleetItemDto>>(IntegrationJsonOptions.Default, ct);
        return page?.Items.ToList() ?? [];
    }

    private static async Task<int> AddPipelineAsync(
        IServiceProvider services, RbacAuditSeed.Tenant tenant, string templateName, int version)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pipeline = new Pipeline
        {
            Name = $"{tenant.Marker}-ci",
            ProjectId = tenant.ProjectId,
            YamlDefinition = $"name: ci\nextends: {templateName}@{version}\nstages: []\n",
            TemplateReferenceName = templateName,
            TemplateReferenceVersion = version,
            CreatedByUsername = "admin"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return pipeline.Id;
    }

    private static async Task<List<PipelineTemplateSummaryDto>> ListAsync(HttpClient http)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await http.GetAsync("/api/pipelines/templates", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<List<PipelineTemplateSummaryDto>>(IntegrationJsonOptions.Default, ct) ?? [];
    }

    private static async Task<(int OrganizationId, int Id, int LatestVersion)> PlatformTemplateAsync(
        IServiceProvider services, string name)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = await db.PipelineTemplates.AsNoTracking()
            .Where(row => row.Organization.Slug == PlatformSlug && row.Name == name)
            .Select(row => new { row.OrganizationId, row.Id, row.LatestVersion })
            .SingleAsync(TestContext.Current.CancellationToken);
        return (template.OrganizationId, template.Id, template.LatestVersion);
    }

    private static async Task<int> AddTemplateAsync(IServiceProvider services, int organizationId, string name)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new PipelineTemplate
        {
            Name = name,
            Category = "Custom",
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 1, YamlContent = RbacAuditSeed.PipelineYaml, ChangelogEntry = "Initial version",
                    CreatedAt = DateTime.UtcNow, CreatedByUsername = "admin"
                }
            ]
        };
        db.PipelineTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return template.Id;
    }
}
