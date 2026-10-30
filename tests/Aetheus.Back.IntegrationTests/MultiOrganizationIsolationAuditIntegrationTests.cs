// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Multi-organization isolation audit (ADR-059, 2026-10-28), on the real host and PostgreSQL: every
/// finding has one test here that failed before its fix. Organization A is the victim, B the caller's
/// own; the shipped delivery templates live in the platform organization (slug <c>aetheus</c>) and are
/// extended from every organization.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MultiOrganizationIsolationAuditIntegrationTests(PostgresFixture fixture)
{
    private const string PlatformSlug = "aetheus";
    private const string SharedTemplate = "application-light";

    // --- Finding 1 (high): a shipped catalog template is a site Admin's, not the platform organization's ---

    [Fact]
    public async Task SharedCatalogTemplate_ContributorOfThePlatformOrganization_CannotCreateUpdatePromoteOrDeleteIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (platformId, templateId, latest) = await PlatformTemplateAsync(factory.Services, SharedTemplate);
        // Referenced by nothing, so a delete would not stop on a reference conflict.
        var (_, unreferencedId, _) = await PlatformTemplateAsync(factory.Services, "package-web-analytics-browser");
        var (offCatalogPipelineId, extendingPipelineId) = await SeedPlatformPipelinesAsync(factory.Services, platformId);
        var contributor = await RbacAuditSeed.CreateUserAsync(factory.Services, "platform", (RoleNames.Contributor, platformId));
        using var client = await IntegrationUsers.LoginClientAsync(factory, contributor.Username);

        var writes = new (string Label, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("update", () => client.PutAsJsonAsync($"/api/pipelines/templates/{templateId}", new UpdatePipelineTemplateRequest
            {
                Name = SharedTemplate, Category = "Application Delivery", YamlContent = RbacAuditSeed.PipelineYaml, ChangelogEntry = "attacker"
            }, ct)),
            ("delete", () => client.DeleteAsync($"/api/pipelines/templates/{unreferencedId}", ct)),
            ("create a case variant", () => client.PostAsJsonAsync("/api/pipelines/templates", new CreatePipelineTemplateRequest
            {
                Name = "Application-Light", Category = "x", YamlContent = RbacAuditSeed.PipelineYaml, OrganizationId = platformId
            }, ct)),
            ("import a case variant", () => client.PostAsync("/api/pipelines/templates/import", YamlFile("name: APPLICATION-LIGHT\nstages: []\n"), ct)),
            ("fleet promote", () => client.PostAsJsonAsync($"/api/pipelines/{extendingPipelineId}/promote-template", new PromotePipelineTemplateRequest
            {
                YamlContent = RbacAuditSeed.PipelineYaml, ChangelogEntry = "attacker"
            }, ct)),
            ("fleet extract as a case variant", () => client.PostAsJsonAsync($"/api/pipelines/{offCatalogPipelineId}/extract-template", new ExtractPipelineTemplateRequest
            {
                TemplateName = "Application-LIGHT", RewritePipeline = false
            }, ct)),
        };
        var leaks = new List<string>();
        foreach (var (label, send) in writes)
        {
            using var response = await send();
            if (response.StatusCode != HttpStatusCode.Forbidden)
                leaks.Add($"{label}: {(int)response.StatusCode}");
        }
        Assert.True(leaks.Count == 0, string.Join("; ", leaks));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(latest, (await db.PipelineTemplates.SingleAsync(row => row.Id == templateId, ct)).LatestVersion);
            Assert.Equal(1, await db.PipelineTemplates.CountAsync(row => row.Name.ToLower() == SharedTemplate, ct));
            Assert.True(await db.PipelineTemplates.AnyAsync(row => row.Id == unreferencedId, ct));
        }

        // The refusal is the catalog's, not a blanket one: the same contributor still writes its own templates,
        // and a site Admin still versions the shipped one.
        using var own = await client.PostAsJsonAsync("/api/pipelines/templates", new CreatePipelineTemplateRequest
        {
            Name = $"own-{Guid.NewGuid():N}",
            Category = "x",
            YamlContent = RbacAuditSeed.PipelineYaml,
            OrganizationId = platformId
        }, ct);
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        using var admin = await IntegrationUsers.AdminClientAsync(factory);
        using var adminUpdate = await admin.PutAsJsonAsync($"/api/pipelines/templates/{templateId}", new UpdatePipelineTemplateRequest
        {
            Name = SharedTemplate,
            Category = "Application Delivery",
            YamlContent = RbacAuditSeed.PipelineYaml,
            ChangelogEntry = "admin"
        }, ct);
        Assert.Equal(HttpStatusCode.OK, adminUpdate.StatusCode);
    }

    [Fact]
    public async Task SharedCatalogTemplate_Delete_CountsTheReferencesOfEveryOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var (_, templateId, _) = await PlatformTemplateAsync(factory.Services, "package-telemetry");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Pipelines.Add(new Pipeline
            {
                Name = $"{b.Marker}-extends",
                ProjectId = b.ProjectId,
                YamlDefinition = "name: extends\nextends: package-telemetry@1\nstages: []\n",
                CreatedByUsername = "admin"
            });
            await db.SaveChangesAsync(ct);
        }
        using var admin = await IntegrationUsers.AdminClientAsync(factory);

        using var response = await admin.DeleteAsync($"/api/pipelines/templates/{templateId}", ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var verify = factory.Services.CreateScope();
        Assert.True(await verify.ServiceProvider.GetRequiredService<AppDbContext>().PipelineTemplates.AnyAsync(row => row.Id == templateId, ct));
    }

    // --- Finding 2 (medium): an agent probe never uses a server of another organization ---

    [Fact]
    public async Task MonitoredApp_CallerOfBothOrganizations_CannotProbeFromAServerOfTheOtherOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var caller = await RbacAuditSeed.CreateUserAsync(factory.Services, "probe",
            (RoleNames.Contributor, a.OrganizationId), (RoleNames.Contributor, b.OrganizationId));
        int appId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var app = new MonitoredApp
            {
                Name = $"{b.Marker}-app",
                ProjectId = b.ProjectId,
                ServerId = b.ServerId,
                ProbeUrl = "http://localhost/health",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.MonitoredApps.Add(app);
            await db.SaveChangesAsync(ct);
            appId = app.Id;
        }
        using var client = await IntegrationUsers.LoginClientAsync(factory, caller.Username);

        using var created = await client.PostAsJsonAsync($"/api/appmonitoring/projects/{b.ProjectId}/apps", new CreateMonitoredAppRequest
        {
            Name = $"{b.Marker}-cross",
            ProjectId = b.ProjectId,
            ServerId = a.ServerId,
            ProbeUrl = "http://localhost/health"
        }, ct);
        using var updated = await client.PutAsJsonAsync($"/api/appmonitoring/apps/{appId}", new UpdateMonitoredAppRequest
        {
            Name = $"{b.Marker}-app",
            ServerId = a.ServerId,
            ProbeUrl = "http://localhost/health"
        }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, updated.StatusCode);
        using var verify = factory.Services.CreateScope();
        var rows = verify.ServiceProvider.GetRequiredService<AppDbContext>().MonitoredApps;
        Assert.False(await rows.AnyAsync(row => row.ServerId == a.ServerId, ct));
    }

    // --- Finding 3 (medium): a work item names only members of its organization and links only its own runs ---

    [Fact]
    public async Task WorkItem_AssigneeAndLinkedRun_StayInTheProjectsOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var b = await RbacAuditSeed.SeedTenantAsync(factory.Services, "b");
        var outsider = await RbacAuditSeed.CreateUserAsync(factory.Services, "outsider", (RoleNames.Reader, a.OrganizationId));
        var caller = await RbacAuditSeed.CreateUserAsync(factory.Services, "assigner", (RoleNames.Contributor, b.OrganizationId));
        int runOfA, legacyId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = new PipelineRun { PipelineId = a.PipelineId, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow, BranchName = "main" };
            // A row stored before the fix: its assignee is outside B.
            var legacy = new WorkItem { ProjectId = b.ProjectId, Title = "legacy", Tags = string.Empty, AssigneeUserId = outsider.Id };
            db.AddRange(run, legacy);
            await db.SaveChangesAsync(ct);
            (runOfA, legacyId) = (run.Id, legacy.Id);
        }
        using var client = await IntegrationUsers.LoginClientAsync(factory, caller.Username);

        using var assigned = await client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { ProjectId = b.ProjectId, Title = "x", AssigneeUserId = outsider.Id }, ct);
        using var linked = await client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { ProjectId = b.ProjectId, Title = "x", LinkedPipelineRunId = runOfA }, ct);
        using var reassigned = await client.PutAsJsonAsync($"/api/work-items/{legacyId}", new UpdateWorkItemRequest { Title = "legacy", AssigneeUserId = caller.Id, LinkedPipelineRunId = runOfA }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, assigned.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, linked.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reassigned.StatusCode);

        // The legacy row never names the outsider, in the detail, the list or the board.
        var detail = await client.GetFromJsonAsync<WorkItemDetailDto>($"/api/work-items/{legacyId}", IntegrationJsonOptions.Default, ct);
        Assert.Null(detail!.AssigneeName);
        var listJson = await client.GetStringAsync($"/api/work-items?projectId={b.ProjectId}&pageSize=100", ct);
        var boardJson = await client.GetStringAsync($"/api/work-items/board/{b.ProjectId}", ct);
        Assert.DoesNotContain(outsider.Username, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain(outsider.Username, boardJson, StringComparison.Ordinal);

        // A member of B is still assigned and named.
        using var own = await client.PostAsJsonAsync("/api/work-items", new CreateWorkItemRequest { ProjectId = b.ProjectId, Title = "own", AssigneeUserId = caller.Id }, ct);
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        var ownId = (await own.Content.ReadFromJsonAsync<WorkItemDto>(IntegrationJsonOptions.Default, ct))!.Id;
        var ownDetail = await client.GetFromJsonAsync<WorkItemDetailDto>($"/api/work-items/{ownId}", IntegrationJsonOptions.Default, ct);
        Assert.Equal(caller.Username, ownDetail!.AssigneeName);

        using var verify = factory.Services.CreateScope();
        var items = verify.ServiceProvider.GetRequiredService<AppDbContext>().WorkItems;
        Assert.False(await items.AnyAsync(row => row.LinkedPipelineRunId == runOfA, ct));
        Assert.Equal(1, await items.CountAsync(row => row.AssigneeUserId == outsider.Id, ct));
    }

    // --- Finding 4 (low): membership alone lists no AI profile and validates nothing in an organization ---

    [Fact]
    public async Task MemberWithoutRole_GetsNoProfileOption_AndCannotValidateInTheOrganization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var member = await RbacAuditSeed.CreateUserAsync(factory.Services, "bare");
        int profileId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<Aetheus.Back.Services.IEncryptionService>();
            db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = a.OrganizationId, UserId = member.Id, Role = OrganizationRole.Member });
            var profile = new AiRunnerProfile
            {
                OrganizationId = a.OrganizationId,
                Name = $"{a.Marker}-profile",
                Provider = "anthropic",
                Model = "claude-sonnet-5-5",
                ApiKeyEncrypted = encryption.EncryptValue("sk-integration-0123456789"),
                EnvironmentJsonEncrypted = encryption.EncryptValue("{}"),
                MaxOutputTokens = 16_000
            };
            db.AiRunnerProfiles.Add(profile);
            await db.SaveChangesAsync(ct);
            profileId = profile.Id;
        }
        using var client = await IntegrationUsers.LoginClientAsync(factory, member.Username);

        var options = await IntegrationUsers.ReadListIdsAsync(await client.GetAsync("/api/ai/profile-options", ct));
        using var validated = await client.PostAsJsonAsync("/api/pipelines/validate",
            new ValidateYamlRequest { Yaml = RbacAuditSeed.PipelineYaml, OrganizationId = a.OrganizationId }, ct);

        Assert.DoesNotContain(profileId, options);
        Assert.Equal(HttpStatusCode.Forbidden, validated.StatusCode);

        // A Reader of A (a role, not membership alone) gets both.
        var reader = await RbacAuditSeed.CreateUserAsync(factory.Services, "reader", (RoleNames.Reader, a.OrganizationId));
        using var readerClient = await IntegrationUsers.LoginClientAsync(factory, reader.Username);
        Assert.Contains(profileId, await IntegrationUsers.ReadListIdsAsync(await readerClient.GetAsync("/api/ai/profile-options", ct)));
        using var readerValidated = await readerClient.PostAsJsonAsync("/api/pipelines/validate",
            new ValidateYamlRequest { Yaml = RbacAuditSeed.PipelineYaml, OrganizationId = a.OrganizationId }, ct);
        Assert.Equal(HttpStatusCode.OK, readerValidated.StatusCode);
    }

    // --- Finding 5 (low): linking a project server into an environment needs Write on its project ---

    [Fact]
    public async Task EnvironmentLink_ReaderOfTheProjectServersProject_CannotEnlistIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        var a = await RbacAuditSeed.SeedTenantAsync(factory.Services, "a");
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "envlink",
            [
                new IntegrationUsers.Grant(ResourceType.Environment, a.EnvironmentId, Permission.Write),
                new IntegrationUsers.Grant(ResourceType.Project, null, Permission.Read)
            ],
            a.OrganizationId);
        var contributor = await RbacAuditSeed.CreateUserAsync(factory.Services, "envlink-w", (RoleNames.Contributor, a.OrganizationId));
        var route = $"/api/environments/{a.EnvironmentId}/project-servers/{a.ProjectServerId}";
        using var readerClient = await IntegrationUsers.LoginClientAsync(factory, reader.Username);
        using var contributorClient = await IntegrationUsers.LoginClientAsync(factory, contributor.Username);

        using var refused = await readerClient.PostAsync(route, null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var accepted = await contributorClient.PostAsync(route, null, ct);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    private static async Task<(int OrganizationId, int TemplateId, int LatestVersion)> PlatformTemplateAsync(
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

    private static async Task<(int OffCatalogPipelineId, int ExtendingPipelineId)> SeedPlatformPipelinesAsync(
        IServiceProvider services, int platformId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new Project { Name = $"platform-{Guid.NewGuid():N}", OrganizationId = platformId };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var offCatalog = new Pipeline { Name = $"off-{Guid.NewGuid():N}", ProjectId = project.Id, YamlDefinition = RbacAuditSeed.PipelineYaml, CreatedByUsername = "admin" };
        var extending = new Pipeline
        {
            Name = $"ext-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            YamlDefinition = $"name: ext\nextends: {SharedTemplate}@1\nstages: []\n",
            CreatedByUsername = "admin"
        };
        db.Pipelines.AddRange(offCatalog, extending);
        await db.SaveChangesAsync(ct);
        return (offCatalog.Id, extending.Id);
    }

    private static MultipartFormDataContent YamlFile(string yaml) =>
        new() { { new ByteArrayContent(Encoding.UTF8.GetBytes(yaml)), "file", "template.yaml" } };
}
