// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Shared.Components.Organizations;
namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Deep create → read → list → delete round-trips for a representative set of CRUD APIs,
/// driven entirely over HTTP as the bootstrap admin. Where the breadth sweeps prove every
/// endpoint is reachable and auth-gated, these prove the full write/read/delete lifecycle of
/// a resource survives a real relational round-trip: the row is persisted, projected back
/// through the read path, and genuinely removed (a follow-up GET returns 404). Each test uses
/// GUID-suffixed names so the shared seeded database stays collision-free across runs.
/// </summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class ApiCrudRoundTripTests(ApiSmokeFixture fixture)
{
    [Fact]
    public async Task AgentPool_CreateReadDelete_RoundTrips()
    {
        using var client = fixture.CreateAdminClient();
        var name = $"pool-{Guid.NewGuid():N}";

        var createResp = await client.PostAsJsonAsync("/api/agent-pools", new CreateAgentPoolRequest
        {
            Name = name,
            Description = "smoke round-trip",
            MaxConcurrency = 2
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var pool = await createResp.Content.ReadFromJsonAsync<AgentPoolDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(pool);

        var getResp = await client.GetAsync($"/api/agent-pools/{pool!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<AgentPoolDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(name, fetched!.Name);
        Assert.Equal(2, fetched.MaxConcurrency);
        await AssertListedAsync<AgentPoolDto>(client, "/api/agent-pools", pool.Id, static item => item.Id);

        var delResp = await client.DeleteAsync($"/api/agent-pools/{pool.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await client.GetAsync($"/api/agent-pools/{pool.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    [Fact]
    public async Task Environment_CreateReadDelete_RoundTrips()
    {
        using var client = fixture.CreateAdminClient();
        var name = $"env-{Guid.NewGuid():N}";

        var createResp = await client.PostAsJsonAsync("/api/environments", new CreateEnvironmentRequest
        {
            Name = name,
            Description = "smoke round-trip"
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var env = await createResp.Content.ReadFromJsonAsync<EnvironmentDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(env);

        var getResp = await client.GetAsync($"/api/environments/{env!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<EnvironmentDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(name, fetched!.Name);
        await AssertListedAsync<EnvironmentDto>(client, "/api/environments", env.Id, static item => item.Id);

        var delResp = await client.DeleteAsync($"/api/environments/{env.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await client.GetAsync($"/api/environments/{env.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    [Fact]
    public async Task Project_CreateReadDelete_RoundTrips()
    {
        using var client = fixture.CreateAdminClient();
        var name = $"project-{Guid.NewGuid():N}";

        var createResp = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = name,
            Description = "smoke round-trip",
            DefaultBranch = "develop",
            Tags = ["integration"]
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var project = await createResp.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(project);

        var getResp = await client.GetAsync($"/api/projects/{project!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<ProjectDetailDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(name, fetched!.Name);
        Assert.Equal("smoke round-trip", fetched.Description);
        Assert.Equal("develop", fetched.DefaultBranch);
        Assert.Contains("integration", fetched.Tags);
        await AssertListedAsync<ProjectDto>(client, "/api/projects", project.Id, static item => item.Id);

        var delResp = await client.DeleteAsync($"/api/projects/{project.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await client.GetAsync($"/api/projects/{project.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    [Fact]
    public async Task Organization_CreateReadDelete_RoundTrips()
    {
        using var client = fixture.CreateAdminClient();

        var suffix = Guid.NewGuid().ToString("N");
        var name = $"Smoke Org {suffix}";
        var slug = $"smoke-{suffix}";
        var createResp = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest
        {
            Name = name,
            Slug = slug,
            Description = "smoke round-trip"
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var org = await createResp.Content.ReadFromJsonAsync<OrganizationDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(org);

        var getResp = await client.GetAsync($"/api/organizations/{org!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<OrganizationDetailDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(name, fetched!.Name);
        Assert.Equal(slug, fetched.Slug);
        await AssertListedAsync<OrganizationDto>(client, "/api/organizations", org.Id, static item => item.Id);

        var delResp = await client.DeleteAsync($"/api/organizations/{org.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await client.GetAsync($"/api/organizations/{org.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    [Fact]
    public async Task User_CreateReadDelete_RoundTrips()
    {
        using var client = fixture.CreateAdminClient();
        var username = $"smoke-user-{Guid.NewGuid():N}";

        var createResp = await client.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            Username = username,
            Email = $"{username}@example.com",
            OrganizationRoles = await IntegrationUsers.InDefaultOrganizationAsync(client, "Reader")
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var user = await createResp.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(user);

        var getResp = await client.GetAsync($"/api/users/{user!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<UserDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(username, fetched!.Username);
        await AssertListedAsync<UserDto>(client, "/api/users", user.Id, static item => item.Id);

        var delResp = await client.DeleteAsync($"/api/users/{user.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await client.GetAsync($"/api/users/{user.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    /// <summary>
    /// Vault carries the real business risk the simple non-owned entities don't: it is
    /// <c>[AtMostOneOwner]</c>, org-scoped, secret-masking, and FK Restrict-on-delete. This
    /// round-trip exercises that whole owned-entity create → read → delete path end-to-end over
    /// HTTP, owning the vault by the seeded project (S-FEAT-OWN5).
    /// </summary>
    [Fact]
    public async Task Vault_CreateReadDelete_RoundTrips()
    {
        using var client = fixture.CreateAdminClient();
        var name = $"vault-{Guid.NewGuid():N}";

        var createResp = await client.PostAsJsonAsync("/api/vaults", new CreateVaultRequest
        {
            Name = name,
            Description = "owned-entity smoke round-trip",
            ProjectId = fixture.ProjectId
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var vault = await createResp.Content.ReadFromJsonAsync<VaultDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(vault);

        var getResp = await client.GetAsync($"/api/vaults/{vault!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<VaultDetailDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(name, fetched!.Name);
        Assert.Equal(fixture.ProjectId, fetched.ProjectId);
        await AssertListedAsync<VaultDto>(client, "/api/vaults", vault.Id, static item => item.Id);

        var delResp = await client.DeleteAsync($"/api/vaults/{vault.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await client.GetAsync($"/api/vaults/{vault.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    /// <summary>
    /// Variable library is the second <c>[AtMostOneOwner]</c>, org-scoped, FK Restrict-on-delete
    /// owned entity (S-FEAT-OWN5). Same create → read → delete → confirm-gone lifecycle as the vault
    /// round-trip, owning the library by the seeded project so the org-scoped ownership path and the
    /// Restrict-on-delete FK are exercised end-to-end over HTTP.
    /// </summary>
    [Fact]
    public async Task VariableLibrary_CreateReadDelete_RoundTrips()
    {
        using var client = fixture.CreateAdminClient();
        var name = $"varlib-{Guid.NewGuid():N}";

        var createResp = await client.PostAsJsonAsync("/api/variable-libraries", new CreateVariableLibraryRequest
        {
            Name = name,
            Description = "owned-entity smoke round-trip",
            ProjectId = fixture.ProjectId
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var library = await createResp.Content.ReadFromJsonAsync<VariableLibraryDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(library);

        var getResp = await client.GetAsync($"/api/variable-libraries/{library!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<VariableLibraryDetailDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(name, fetched!.Name);
        Assert.Equal(fixture.ProjectId, fetched.ProjectId);
        await AssertListedAsync<VariableLibraryDto>(client, "/api/variable-libraries", library.Id, static item => item.Id);

        var delResp = await client.DeleteAsync($"/api/variable-libraries/{library.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await client.GetAsync($"/api/variable-libraries/{library.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    /// <summary>
    /// Pipeline is the third <c>[AtMostOneOwner]</c>, org-scoped, FK Restrict-on-delete owned entity
    /// (S-FEAT-OWN5). Unlike vault/variable-library, create additionally runs strict YAML validation,
    /// so the payload carries a minimal valid one-stage/one-step definition; an invalid one would 400.
    /// Same create → read → delete → confirm-gone lifecycle, owned by the seeded project.
    /// </summary>
    [Fact]
    public async Task Pipeline_CreateReadDelete_RoundTrips()
    {
        using var client = fixture.CreateAdminClient();
        var name = $"pipeline-{Guid.NewGuid():N}";

        const string yaml =
            "name: smoke\n" +
            "trigger: manual\n" +
            "stages:\n" +
            "  - name: build\n" +
            "    agent: any\n" +
            "    steps:\n" +
            "      - name: echo\n" +
            "        shell: echo hi\n";

        var createResp = await client.PostAsJsonAsync("/api/pipelines", new CreatePipelineRequest
        {
            Name = name,
            Description = "owned-entity smoke round-trip",
            YamlDefinition = yaml,
            ProjectId = fixture.ProjectId
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var pipeline = await createResp.Content.ReadFromJsonAsync<PipelineDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(pipeline);

        var getResp = await client.GetAsync($"/api/pipelines/{pipeline!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = await getResp.Content.ReadFromJsonAsync<PipelineDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(name, fetched!.Name);
        Assert.Equal(fixture.ProjectId, fetched.ProjectId);
        await AssertListedAsync<PipelineDto>(client, "/api/pipelines", pipeline.Id, static item => item.Id);

        var delResp = await client.DeleteAsync($"/api/pipelines/{pipeline.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        var getAfter = await client.GetAsync($"/api/pipelines/{pipeline.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
    }

    /// <summary>
    /// Negative path: a body failing DataAnnotations validation (<c>[Required]</c> Name) must be
    /// rejected with 400 by the real model-binding/validation pipeline, end-to-end - not silently
    /// persisted. The breadth/round-trip happy paths never exercise the rejection branch.
    /// </summary>
    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        using var client = fixture.CreateAdminClient();

        var resp = await client.PostAsJsonAsync("/api/agent-pools", new CreateAgentPoolRequest
        {
            Name = "", // [Required] - must trip 400 validation
            Description = "invalid-body smoke",
            MaxConcurrency = 2
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// R-22: the slug is optional. Two organizations created with the same name-derived slug and no slug of their
    /// own get distinct, generated slugs over real HTTP and PostgreSQL (unique index respected).
    /// </summary>
    [Fact]
    public async Task Organization_CreatedWithoutSlug_GetsAGeneratedUniqueSlug()
    {
        using var client = fixture.CreateAdminClient();
        var token = Guid.NewGuid().ToString("N")[..8];
        var name = $"Sans Slug {token}";

        var first = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest { Name = name },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstOrg = await first.Content.ReadFromJsonAsync<OrganizationDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(firstOrg);
        Assert.Equal($"sans-slug-{token}", firstOrg.Slug);

        // Same derived slug (different case and punctuation in the name, so the name itself is not a duplicate).
        var second = await client.PostAsJsonAsync("/api/organizations", new CreateOrganizationRequest { Name = $"sans slug {token}!" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondOrg = await second.Content.ReadFromJsonAsync<OrganizationDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(secondOrg);
        Assert.Equal($"sans-slug-{token}-2", secondOrg.Slug);

        await client.DeleteAsync($"/api/organizations/{firstOrg.Id}", TestContext.Current.CancellationToken);
        await client.DeleteAsync($"/api/organizations/{secondOrg.Id}", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Negative path: creating a second resource with a colliding unique key must surface the
    /// <c>ConflictException</c> as a 409 through the error-handling middleware - proving the
    /// duplicate-key guard actually fires over HTTP rather than 500-ing or silently upserting.
    /// </summary>
    [Fact]
    public async Task Create_DuplicateSlug_Returns409()
    {
        using var client = fixture.CreateAdminClient();

        var suffix = Guid.NewGuid().ToString("N");
        var request = new CreateOrganizationRequest
        {
            Name = $"Dup Org {suffix}",
            Slug = $"dup-{suffix}",
            Description = "duplicate-slug smoke"
        };

        var first = await client.PostAsJsonAsync("/api/organizations", request, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<OrganizationDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);

        var second = await client.PostAsJsonAsync("/api/organizations", request, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Additive-tests contract (see ApiSmokeFixture): delete what we created so the shared DB
        // stays collision-free and no orphan org leaks into other suites' reads.
        var delResp = await client.DeleteAsync($"/api/organizations/{created!.Id}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);
    }

    private static async Task AssertListedAsync<T>(
        HttpClient client,
        string path,
        int id,
        Func<T, int> idSelector)
    {
        var page = await client.GetFromJsonAsync<PaginatedResult<T>>(
            $"{path}?page=1&pageSize=200",
            IntegrationJsonOptions.Default,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Contains(page.Items, item => idSelector(item) == id);
    }
}
