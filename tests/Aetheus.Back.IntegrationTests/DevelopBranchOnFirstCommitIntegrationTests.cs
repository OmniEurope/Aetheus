// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-76 (owner decision 2026-10-28), through the real API on PostgreSQL with a real bare repository on disk: a project of an
/// organization that enabled "create a develop branch" gets one, cut from its default branch, when its internal repository
/// receives its first commit (here a pipeline saved through Aetheus, which commits its definition); a project of an
/// organization that did not gets none. A branch deleted afterwards is not brought back by the next commit.
/// </summary>
[Collection(ApiSmokeCollection.Name)]
public sealed class DevelopBranchOnFirstCommitIntegrationTests(ApiSmokeFixture fixture)
{
    private const string PipelineYaml =
        "name: {0}\ntrigger: manual\nstages:\n  - name: build\n    agent: any\n    steps:\n      - name: echo\n        shell: echo hi\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrganizationWithTheSettingOn_GetsADevelopBranchFromTheFirstCommit_AndOnlyFromTheFirst()
    {
        using var client = fixture.CreateAdminClient();
        var scenario = await SetUpAsync(client, autoCreateDevelopBranch: true);
        try
        {
            Assert.Empty(await BranchesAsync(client, scenario.RepositoryId));

            await CommitPipelineAsync(client, scenario);

            Assert.Equal(["develop", "main"], await BranchesAsync(client, scenario.RepositoryId));

            // Deleted on purpose, develop stays deleted: only the first commit asks for it.
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.DeleteAsync($"/api/git/repos/{scenario.RepositoryId}/branches/develop", Ct)).StatusCode);
            await CommitPipelineAsync(client, scenario);
            Assert.Equal(["main"], await BranchesAsync(client, scenario.RepositoryId));
        }
        finally
        {
            await TearDownAsync(client, scenario);
        }
    }

    [Fact]
    public async Task OrganizationWithTheSettingOff_KeepsOnlyTheDefaultBranch()
    {
        using var client = fixture.CreateAdminClient();
        var scenario = await SetUpAsync(client, autoCreateDevelopBranch: false);
        try
        {
            await CommitPipelineAsync(client, scenario);

            Assert.Equal(["main"], await BranchesAsync(client, scenario.RepositoryId));
        }
        finally
        {
            await TearDownAsync(client, scenario);
        }
    }

    private sealed class Scenario
    {
        public int OrganizationId { get; init; }
        public int ProjectId { get; init; }
        public int RepositoryId { get; init; }
        public string Suffix { get; init; } = string.Empty;
        public List<int> PipelineIds { get; } = [];
    }

    private static async Task<Scenario> SetUpAsync(HttpClient client, bool autoCreateDevelopBranch)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var organization = await PostAsync<OrganizationDto>(client, "/api/organizations", new CreateOrganizationRequest
        {
            Name = $"Develop {suffix}",
            Slug = $"develop-{suffix}",
            AutoCreateDevelopBranch = autoCreateDevelopBranch
        });
        var project = await PostAsync<ProjectDto>(client, "/api/projects", new CreateProjectRequest
        {
            Name = $"develop-project-{suffix}",
            Description = "develop branch on first commit",
            OrganizationId = organization.Id
        });
        var repository = await PostAsync<GitLightRepoDto>(client, "/api/git/repos", new CreateGitLightRepoRequest
        {
            ProjectId = project.Id,
            Name = $"develop-repo-{suffix}",
            DefaultBranch = "main"
        });
        return new Scenario { OrganizationId = organization.Id, ProjectId = project.Id, RepositoryId = repository.Id, Suffix = suffix };
    }

    private static async Task CommitPipelineAsync(HttpClient client, Scenario scenario)
    {
        var name = $"develop-ci-{scenario.Suffix}-{scenario.PipelineIds.Count}";
        var pipeline = await PostAsync<PipelineDto>(client, "/api/pipelines", new CreatePipelineRequest
        {
            Name = name,
            Description = "first commit of the repository",
            ProjectId = scenario.ProjectId,
            SourceBranch = "main",
            YamlDefinition = string.Format(System.Globalization.CultureInfo.InvariantCulture, PipelineYaml, name)
        });
        scenario.PipelineIds.Add(pipeline.Id);
    }

    private static async Task<List<string>> BranchesAsync(HttpClient client, int repositoryId)
    {
        var response = await client.GetAsync($"/api/git/repos/{repositoryId}/branches?page=1&pageSize=50", Ct);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PaginatedResult<GitLightBranchDto>>(IntegrationJsonOptions.Default, Ct);
        return [.. Assert.IsType<PaginatedResult<GitLightBranchDto>>(page).Items.Select(branch => branch.Name).Order(StringComparer.Ordinal)];
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {await response.Content.ReadAsStringAsync(Ct)}");
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(IntegrationJsonOptions.Default, Ct));
    }

    private static async Task TearDownAsync(HttpClient client, Scenario scenario)
    {
        foreach (var id in scenario.PipelineIds) await client.DeleteAsync($"/api/pipelines/{id}", Ct);
        await client.DeleteAsync($"/api/git/repos/{scenario.RepositoryId}", Ct);
        await client.DeleteAsync($"/api/projects/{scenario.ProjectId}", Ct);
        await client.DeleteAsync($"/api/organizations/{scenario.OrganizationId}", Ct);
    }
}
