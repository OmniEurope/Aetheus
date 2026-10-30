// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;
using Aetheus.Back.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

[Collection(ApiSmokeCollection.Name)]
public sealed class PipelineFleetIntegrationTests(ApiSmokeFixture fixture)
{
    [Fact]
    public async Task PromoteFromOnePipeline_MarksPeerOutdatedAndPeerCanAdoptVersion()
    {
        using var client = fixture.CreateAdminClient();
        var suffix = Guid.NewGuid().ToString("N");
        var projectResponse = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"promote-{suffix}",
            Description = "pipeline promotion integration"
        }, cancellationToken: TestContext.Current.CancellationToken);
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(project);

        GitLightRepoDto? repository = null;
        PipelineTemplateDto? template = null;
        PipelineDto? source = null;
        PipelineDto? peer = null;
        try
        {
            var repositoryResponse = await client.PostAsJsonAsync("/api/git/repos", new CreateGitLightRepoRequest
            {
                ProjectId = project.Id,
                Name = $"promote-repo-{suffix}",
                DefaultBranch = "main"
            }, cancellationToken: TestContext.Current.CancellationToken);
            repositoryResponse.EnsureSuccessStatusCode();
            repository = await repositoryResponse.Content.ReadFromJsonAsync<GitLightRepoDto>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(repository);

            var templateResponse = await client.PostAsJsonAsync("/api/pipelines/templates",
                new CreatePipelineTemplateRequest
                {
                    Name = $"promote-ci-{suffix}",
                    Category = "CI",
                    YamlContent = "name: promote-ci\nstages:\n  - name: build\n    agent: any\n    steps:\n      - name: compile\n        shell: dotnet --info",
                    ChangelogEntry = "Initial version"
                }, cancellationToken: TestContext.Current.CancellationToken);
            templateResponse.EnsureSuccessStatusCode();
            template = await templateResponse.Content.ReadFromJsonAsync<PipelineTemplateDto>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(template);

            source = await CreatePipelineAsync(client, project.Id, $"source-{suffix}",
                $"name: source-{suffix}\nextends: {template.Name}@1\nstages:\n  - name: quality\n    agent: any\n    steps:\n      - name: inspect-git\n        shell: git --version");
            peer = await CreatePipelineAsync(client, project.Id, $"peer-{suffix}",
                $"name: peer-{suffix}\nextends: {template.Name}@1\nstages: []");

            var promoteResponse = await client.PostAsJsonAsync(
                $"/api/pipelines/{source.Id}/promote-template",
                new PromotePipelineTemplateRequest
                {
                    ChangelogEntry = "Adopt quality stage from source pipeline",
                    YamlContent = "name: promote-ci\nstages:\n  - name: build\n    agent: any\n    steps:\n      - name: compile\n        shell: dotnet --info\n  - name: quality\n    agent: any\n    steps:\n      - name: inspect-git\n        shell: git --version",
                    RebaseSourcePipeline = true
                }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(promoteResponse.IsSuccessStatusCode,
                await promoteResponse.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
            var promoted = await promoteResponse.Content.ReadFromJsonAsync<PipelineTemplateDto>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(promoted);
            Assert.Equal(2, promoted.Version);

            var fleetResponse = await client.GetAsync(
                $"/api/pipelines/fleet?search={peer.Name}&page=1&pageSize=10", cancellationToken: TestContext.Current.CancellationToken);
            fleetResponse.EnsureSuccessStatusCode();
            var fleet = await fleetResponse.Content.ReadFromJsonAsync<PaginatedResult<PipelineFleetItemDto>>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            var fleetItem = Assert.Single(Assert.IsType<PaginatedResult<PipelineFleetItemDto>>(fleet).Items);
            Assert.Equal(PipelineFleetFreshness.Outdated, fleetItem.Freshness);
            Assert.Equal(1, fleetItem.PinnedVersion);
            Assert.Equal(2, fleetItem.LatestVersion);

            var previewResponse = await client.PostAsJsonAsync(
                $"/api/pipelines/{peer.Id}/fleet-update/preview",
                new PipelineFleetUpdateRequest { TargetVersion = 2 }, cancellationToken: TestContext.Current.CancellationToken);
            previewResponse.EnsureSuccessStatusCode();
            var preview = await previewResponse.Content.ReadFromJsonAsync<PipelineFleetUpdatePreviewDto>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(preview);
            Assert.Contains("git --version", preview.TargetResolvedYaml, StringComparison.Ordinal);

            var updateResponse = await client.PostAsJsonAsync($"/api/pipelines/{peer.Id}/fleet-update",
                new PipelineFleetUpdateRequest
                {
                    TargetVersion = 2,
                    ExpectedSourceYamlHash = preview.SourceYamlHash
                }, cancellationToken: TestContext.Current.CancellationToken);
            updateResponse.EnsureSuccessStatusCode();
            var updatedPeer = await updateResponse.Content.ReadFromJsonAsync<PipelineDto>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(updatedPeer);
            Assert.Contains($"{template.Name}@2", updatedPeer.YamlDefinition, StringComparison.Ordinal);
        }
        finally
        {
            if (peer is not null) await client.DeleteAsync($"/api/pipelines/{peer.Id}", cancellationToken: TestContext.Current.CancellationToken);
            if (source is not null) await client.DeleteAsync($"/api/pipelines/{source.Id}", cancellationToken: TestContext.Current.CancellationToken);
            if (template is not null) await client.DeleteAsync($"/api/pipelines/templates/{template.Id}", cancellationToken: TestContext.Current.CancellationToken);
            if (repository is not null) await client.DeleteAsync($"/api/git/repos/{repository.Id}", cancellationToken: TestContext.Current.CancellationToken);
            await client.DeleteAsync($"/api/projects/{project.Id}", cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FleetBump_PersistsPinAuditAndGitCommit()
    {
        using var client = fixture.CreateAdminClient();
        var suffix = Guid.NewGuid().ToString("N");

        var projectResponse = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"fleet-{suffix}",
            Description = "pipeline fleet integration"
        }, cancellationToken: TestContext.Current.CancellationToken);
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(project);

        GitLightRepoDto? repository = null;
        PipelineTemplateDto? template = null;
        PipelineDto? pipeline = null;
        try
        {
            var repositoryResponse = await client.PostAsJsonAsync("/api/git/repos", new CreateGitLightRepoRequest
            {
                ProjectId = project.Id,
                Name = $"fleet-repo-{suffix}",
                DefaultBranch = "main"
            }, cancellationToken: TestContext.Current.CancellationToken);
            repositoryResponse.EnsureSuccessStatusCode();
            repository = await repositoryResponse.Content.ReadFromJsonAsync<GitLightRepoDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(repository);

            var templateResponse = await client.PostAsJsonAsync("/api/pipelines/templates",
                new CreatePipelineTemplateRequest
                {
                    Name = $"fleet-ci-{suffix}",
                    Category = "CI",
                    YamlContent = "name: fleet-ci\nstages:\n  - name: build\n    agent: any\n    steps:\n      - name: compile\n        shell: echo v1",
                    ChangelogEntry = "Initial version"
                }, cancellationToken: TestContext.Current.CancellationToken);
            templateResponse.EnsureSuccessStatusCode();
            template = await templateResponse.Content.ReadFromJsonAsync<PipelineTemplateDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(template);

            var pipelineName = $"toto-{suffix}";
            var pipelineResponse = await client.PostAsJsonAsync("/api/pipelines", new CreatePipelineRequest
            {
                Name = pipelineName,
                Description = "fleet bump target",
                ProjectId = project.Id,
                SourceBranch = "main",
                YamlDefinition = $"name: {pipelineName}\nextends: {template.Name}@1\nstages: []"
            }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(pipelineResponse.IsSuccessStatusCode,
                await pipelineResponse.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
            pipeline = await pipelineResponse.Content.ReadFromJsonAsync<PipelineDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(pipeline);

            var publishResponse = await client.PutAsJsonAsync($"/api/pipelines/templates/{template.Id}",
                new UpdatePipelineTemplateRequest
                {
                    Name = template.Name,
                    Description = template.Description,
                    Category = template.Category,
                    YamlContent = "name: fleet-ci\nstages:\n  - name: build\n    agent: any\n    steps:\n      - name: compile\n        shell: echo v2",
                    ChangelogEntry = "Compile improvement"
                }, cancellationToken: TestContext.Current.CancellationToken);
            publishResponse.EnsureSuccessStatusCode();

            var fleetResponse = await client.GetAsync($"/api/pipelines/fleet?search={pipelineName}&page=1&pageSize=10", cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(fleetResponse.IsSuccessStatusCode,
                await fleetResponse.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
            var fleet = await fleetResponse.Content.ReadFromJsonAsync<PaginatedResult<PipelineFleetItemDto>>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(fleet);
            var fleetItem = Assert.Single(fleet.Items);
            Assert.Equal(PipelineFleetFreshness.Outdated, fleetItem.Freshness);
            Assert.Equal(1, fleetItem.PinnedVersion);
            Assert.Equal(2, fleetItem.LatestVersion);

            var previewResponse = await client.PostAsJsonAsync(
                $"/api/pipelines/{pipeline.Id}/fleet-update/preview",
                new PipelineFleetUpdateRequest { TargetVersion = 2 }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(previewResponse.IsSuccessStatusCode,
                await previewResponse.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
            var preview = await previewResponse.Content.ReadFromJsonAsync<PipelineFleetUpdatePreviewDto>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(preview);

            var bumpResponse = await client.PostAsJsonAsync($"/api/pipelines/{pipeline.Id}/fleet-update",
                new PipelineFleetUpdateRequest
                {
                    TargetVersion = 2,
                    ExpectedSourceYamlHash = preview.SourceYamlHash
                }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(bumpResponse.IsSuccessStatusCode,
                await bumpResponse.Content.ReadAsStringAsync(cancellationToken: TestContext.Current.CancellationToken));
            var bumped = await bumpResponse.Content.ReadFromJsonAsync<PipelineDto>(IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(bumped);
            Assert.Contains($"{template.Name}@2", bumped.YamlDefinition, StringComparison.Ordinal);

            var commitsResponse = await client.GetAsync($"/api/git/repos/{repository.Id}/commits?page=1&pageSize=10", cancellationToken: TestContext.Current.CancellationToken);
            commitsResponse.EnsureSuccessStatusCode();
            var commits = await commitsResponse.Content.ReadFromJsonAsync<PaginatedResult<GitLightCommitDto>>(
                IntegrationJsonOptions.Default, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(commits);
            Assert.Contains(commits.Items, commit => commit.Message.Contains(
                $"update {pipelineName} via Aetheus", StringComparison.Ordinal));

            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(log =>
                log.EntityType == "Pipeline" && log.EntityId == pipeline.Id && log.Action == "Updated", cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (pipeline is not null) await client.DeleteAsync($"/api/pipelines/{pipeline.Id}", cancellationToken: TestContext.Current.CancellationToken);
            if (template is not null) await client.DeleteAsync($"/api/pipelines/templates/{template.Id}", cancellationToken: TestContext.Current.CancellationToken);
            if (repository is not null) await client.DeleteAsync($"/api/git/repos/{repository.Id}", cancellationToken: TestContext.Current.CancellationToken);
            await client.DeleteAsync($"/api/projects/{project.Id}", cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A template whose required parameter has no default (application-deploy-prod's candidateVersion,
    /// given at each run) still previews and applies its update: the preview has no run to supply it, so the
    /// placeholder stays unresolved instead of refusing the whole update with 400 ParameterRequired.</summary>
    [Fact]
    public async Task FleetUpdate_OfATemplateWithARequiredParameterWithoutDefault_PreviewsAndApplies()
    {
        using var client = fixture.CreateAdminClient();
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var projectResponse = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"required-{suffix}",
            Description = "fleet update with a required parameter"
        }, cancellationToken: ct);
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>(IntegrationJsonOptions.Default, cancellationToken: ct);
        Assert.NotNull(project);

        PipelineTemplateDto? template = null;
        PipelineDto? pipeline = null;
        try
        {
            var templateResponse = await client.PostAsJsonAsync("/api/pipelines/templates",
                new CreatePipelineTemplateRequest
                {
                    Name = $"required-deploy-{suffix}",
                    Category = "Deploy",
                    YamlContent = "name: required-deploy\nparameters:\n  - name: candidateVersion\n    required: true\nstages:\n  - name: deploy\n    agent: any\n    steps:\n      - name: restore\n        shell: echo ${{ parameters.candidateVersion }}",
                    ChangelogEntry = "Initial version"
                }, cancellationToken: ct);
            Assert.True(templateResponse.IsSuccessStatusCode, await templateResponse.Content.ReadAsStringAsync(ct));
            template = await templateResponse.Content.ReadFromJsonAsync<PipelineTemplateDto>(IntegrationJsonOptions.Default, cancellationToken: ct);
            Assert.NotNull(template);
            pipeline = await CreatePipelineAsync(client, project.Id, $"required-{suffix}",
                $"name: required-{suffix}\nextends: {template.Name}@1\nstages: []");

            var previewResponse = await client.PostAsJsonAsync($"/api/pipelines/{pipeline.Id}/fleet-update/preview",
                new PipelineFleetUpdateRequest { TargetVersion = 1 }, cancellationToken: ct);
            Assert.True(previewResponse.IsSuccessStatusCode, await previewResponse.Content.ReadAsStringAsync(ct));
            var preview = await previewResponse.Content.ReadFromJsonAsync<PipelineFleetUpdatePreviewDto>(IntegrationJsonOptions.Default, cancellationToken: ct);
            Assert.NotNull(preview);
            Assert.Contains("parameters.candidateVersion", preview.TargetResolvedYaml, StringComparison.Ordinal);

            var updateResponse = await client.PostAsJsonAsync($"/api/pipelines/{pipeline.Id}/fleet-update",
                new PipelineFleetUpdateRequest { TargetVersion = 1, ExpectedSourceYamlHash = preview.SourceYamlHash }, cancellationToken: ct);
            Assert.True(updateResponse.IsSuccessStatusCode, await updateResponse.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            if (pipeline is not null) await client.DeleteAsync($"/api/pipelines/{pipeline.Id}", cancellationToken: ct);
            if (template is not null) await client.DeleteAsync($"/api/pipelines/templates/{template.Id}", cancellationToken: ct);
            await client.DeleteAsync($"/api/projects/{project.Id}", cancellationToken: ct);
        }
    }

    private static async Task<PipelineDto> CreatePipelineAsync(
        HttpClient client, int projectId, string name, string yaml)
    {
        var response = await client.PostAsJsonAsync("/api/pipelines", new CreatePipelineRequest
        {
            Name = name,
            Description = "fleet promotion participant",
            ProjectId = projectId,
            SourceBranch = "main",
            YamlDefinition = yaml
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return Assert.IsType<PipelineDto>(await response.Content.ReadFromJsonAsync<PipelineDto>(
            IntegrationJsonOptions.Default));
    }
}
