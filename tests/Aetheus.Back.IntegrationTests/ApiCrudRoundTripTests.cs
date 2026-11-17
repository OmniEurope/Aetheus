// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;

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
    [Trait("Category", "RollbackRelevant")]
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

    /// <summary>
    /// R-31: the branch the pipeline setup wizard chose is written to the project's default branch, over real HTTP and a real
    /// relational round-trip, and the overview's read path (<c>GET /api/projects/{id}</c>, the DTO the card shows) returns it.
    /// Other project fields stay as they were.
    /// </summary>
    [Fact]
    [Trait("Category", "RollbackRelevant")]
    public async Task Project_SetDefaultBranchFromTheWizard_IsPersistedAndRead()
    {
        using var client = fixture.CreateAdminClient();
        var createResp = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"project-{Guid.NewGuid():N}",
            Description = "wizard default branch",
            DefaultBranch = "develop",
            Tags = ["keep"]
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var project = (await createResp.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken))!;
        try
        {
            var putResp = await client.PutAsJsonAsync($"/api/projects/{project.Id}/default-branch",
                new SetProjectDefaultBranchRequest { Branch = " main " }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

            var fetched = await client.GetFromJsonAsync<ProjectDetailDto>($"/api/projects/{project.Id}",
                IntegrationJsonOptions.Default, TestContext.Current.CancellationToken);
            Assert.Equal("main", fetched!.DefaultBranch);
            Assert.Equal("wizard default branch", fetched.Description);
            Assert.Contains("keep", fetched.Tags);

            var blank = await client.PutAsJsonAsync($"/api/projects/{project.Id}/default-branch",
                new SetProjectDefaultBranchRequest { Branch = "  " }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
            var missing = await client.PutAsJsonAsync("/api/projects/999999/default-branch",
                new SetProjectDefaultBranchRequest { Branch = "main" }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            using var _ = await client.DeleteAsync($"/api/projects/{project.Id}", TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Vault carries the real business risk the simple non-owned entities don't: it is
    /// <c>[AtMostOneOwner]</c>, org-scoped, secret-masking, and FK Restrict-on-delete. This
    /// round-trip exercises that whole owned-entity create → read → delete path end-to-end over
    /// HTTP, owning the vault by the seeded project (S-FEAT-OWN5).
    /// </summary>
    [Fact]
    [Trait("Category", "RollbackRelevant")]
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
    /// Pipeline is the third <c>[AtMostOneOwner]</c>, org-scoped, FK Restrict-on-delete owned entity
    /// (S-FEAT-OWN5). Unlike vault/variable-library, create additionally runs strict YAML validation,
    /// so the payload carries a minimal valid one-stage/one-step definition; an invalid one would 400.
    /// Same create → read → delete → confirm-gone lifecycle, owned by the seeded project.
    /// </summary>
    [Fact]
    [Trait("Category", "RollbackRelevant")]
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
