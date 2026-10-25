// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class PipelineTemplateRunIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CompletingPrepare_DispatchesResolvedTemplateSteps()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        // Since external Git pipelines (a790fae72) a project with a repository URL and no internal mirror
        // reads its pipeline from that repository; git.example never resolves, and this test is about the
        // template steps, so the repository answers "no pipeline file" and the stored definition is used.
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IGitCliService, NoPipelineFileGitCli>()));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Set<Organization>().Select(item => item.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        var suffix = Guid.NewGuid().ToString("N");

        var server = new Server
        {
            Name = $"template-runner-{suffix}",
            Hostname = $"template-runner-{suffix}",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            LastHeartbeat = DateTime.UtcNow,
            OrganizationId = organizationId,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]"
        };
        var project = new Project
        {
            Name = $"template-project-{suffix}",
            RepositoryUrl = $"https://git.example/{suffix}.git",
            OrganizationId = organizationId
        };
        var template = new PipelineTemplate
        {
            Name = $"template-{suffix}",
            Category = "CI",
            OrganizationId = organizationId,
            LatestVersion = 1
        };
        template.Versions.Add(new PipelineTemplateVersion
        {
            Version = 1,
            YamlContent = "name: template\nstages:\n  - name: Toolchain\n    steps:\n      - name: Inspect Template\n        shell: echo template-ready",
            ChangelogEntry = "Initial version",
            CreatedByUsername = "integration"
        });
        db.AddRange(server, project, template);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var pipeline = new Pipeline
        {
            Name = $"template-pipeline-{suffix}",
            ProjectId = project.Id,
            YamlDefinition = $"name: template-pipeline\nextends: {template.Name}@1\nstages: []"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runService = scope.ServiceProvider.GetRequiredService<IPipelineRunService>();
        var run = await runService.TriggerRunAsync(
            pipeline.Id,
            new Dictionary<string, string>
            {
                ["AETHEUS_SOURCE_COMMIT"] = new('a', 40)
            },
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(run);
        Assert.Contains("echo template-ready", run.YamlSnapshot, StringComparison.Ordinal);

        var prepareTask = await db.Tasks.SingleAsync(item => item.PipelineRunId == run.Id, cancellationToken: TestContext.Current.CancellationToken);
        var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();
        Assert.True(await taskService.StartTaskAsync(prepareTask.Id, ct: TestContext.Current.CancellationToken));
        Assert.True(await taskService.CompleteTaskAsync(prepareTask.Id,
            new TaskResultDto { Status = TaskExecutionStatus.Success, ExitCode = 0 }, ct: TestContext.Current.CancellationToken));

        var userTask = await db.Tasks.AsNoTracking()
            .SingleAsync(item => item.PipelineRunId == run.Id && item.Id != prepareTask.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Inspect Template", userTask.Name);
    }

    private sealed class NoPipelineFileGitCli : IGitCliService
    {
        public Task<List<(string BranchName, string Version)>> ListReleaseBranchesAsync(string repositoryUrl, CancellationToken ct = default) =>
            Task.FromResult(new List<(string BranchName, string Version)>());

        public Task<GitRemoteBranch> ResolveBranchCommitAsync(string repositoryUrl, string? branch, CancellationToken ct = default) =>
            Task.FromResult(new GitRemoteBranch(branch ?? "main", new string('a', 40)));

        public Task<string?> ReadPipelineYamlAsync(string repositoryUrl, string commit, string pipelineName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }
}
