// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-72 (owner decision of 2026-10-28), on the real host and PostgreSQL through <c>POST /api/pipelines/{id}/run</c>:
/// a release without a grade is refused by the deployment gate without the override and deployed with it; a
/// release that keeps no artifact is refused even with it; the override takes Server.Admin on the targets (like
/// any launch) and Write on the project; it is recorded on the run and in the audit log.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReleaseRedeployGateOverrideHttpIntegrationTests(PostgresFixture fixture)
{
    private const string Ungraded = "1.0.0-ungraded";
    private const string NoArtifact = "1.0.1-noartifact";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReleaseWithoutAGrade_IsRefusedWithoutTheOverride()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithoutRemotePipelineFile(baseFactory);
        var seed = await SeedAsync(factory);
        var deployer = await DeployerAsync(factory, seed);
        using var client = await IntegrationUsers.LoginClientAsync(factory, deployer.Username);

        var response = await RunAsync(client, seed.PipelineId, Ungraded, gateOverride: false);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await ReadRunAsync(factory, await RunIdAsync(response));
        Assert.Equal(PipelineStatus.Failed, run.Status);
        Assert.Contains("no sealed assurance grade", run.WarningsJson, StringComparison.Ordinal);
        Assert.Null(run.GateOverrideBy);
        Assert.Null(run.GateOverrideAt);
    }

    [Fact]
    public async Task AReleaseWithoutAGrade_IsDeployedWithTheOverride_AndTheOverrideIsRecorded()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithoutRemotePipelineFile(baseFactory);
        var seed = await SeedAsync(factory);
        var deployer = await DeployerAsync(factory, seed);
        using var client = await IntegrationUsers.LoginClientAsync(factory, deployer.Username);

        var response = await RunAsync(client, seed.PipelineId, Ungraded, gateOverride: true);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var runId = await RunIdAsync(response);
        var run = await ReadRunAsync(factory, runId);
        Assert.NotEqual(PipelineStatus.Failed, run.Status);
        Assert.DoesNotContain("no sealed assurance grade", run.WarningsJson ?? string.Empty, StringComparison.Ordinal);
        Assert.True(await TaskCountAsync(factory, runId) > 0, "The deployment despite the gate dispatched no task.");

        // Recorded on the run, as the run page reads it...
        Assert.Equal(deployer.Username, run.GateOverrideBy);
        Assert.NotNull(run.GateOverrideAt);
        var dto = await client.GetFromJsonAsync<PipelineRunDto>($"/api/pipelines/runs/{runId}", IntegrationJsonOptions.Default, Ct);
        Assert.Equal(deployer.Username, dto!.GateOverrideBy);
        Assert.Equal(DeploymentGateWarningKind.NoGrade, Assert.Single(dto.GateOverrideWarnings).Kind);

        // ...and in the audit log, under the deployer's name.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = Assert.Single(await db.AuditLogs.AsNoTracking()
            .Where(log => log.Action == "PipelineRunGateOverridden" && log.EntityType == "PipelineRun" && log.EntityId == runId)
            .ToListAsync(Ct));
        Assert.Equal(deployer.Username, audit.Username);
        Assert.Contains(Ungraded, audit.Details, StringComparison.Ordinal);
        Assert.Contains(nameof(DeploymentGateWarningKind.NoGrade), audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReleaseWithoutArtifact_IsRefusedEvenWithTheOverride()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithoutRemotePipelineFile(baseFactory);
        var seed = await SeedAsync(factory);
        var deployer = await DeployerAsync(factory, seed);
        using var client = await IntegrationUsers.LoginClientAsync(factory, deployer.Username);

        var response = await RunAsync(client, seed.PipelineId, NoArtifact, gateOverride: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("GateOverrideNoArtifact", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(0, await RunCountAsync(factory, seed.PipelineId));
    }

    [Fact]
    public async Task AUserWithoutServerAdmin_CannotUseTheOverride()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithoutRemotePipelineFile(baseFactory);
        var seed = await SeedAsync(factory);
        var user = await IntegrationUsers.CreateAsync(factory.Services, "no-server-admin",
        [
            new IntegrationUsers.Grant(ResourceType.Pipeline, seed.PipelineId, Permission.Write),
            new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Write)
        ], permissionKeys: [PermissionKeys.PipelineRun]);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var response = await RunAsync(client, seed.PipelineId, Ungraded, gateOverride: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await RunCountAsync(factory, seed.PipelineId));
    }

    [Fact]
    public async Task AUserWithoutWriteOnTheProject_CannotUseTheOverride_ButCanStillLaunch()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithoutRemotePipelineFile(baseFactory);
        var seed = await SeedAsync(factory);
        var user = await IntegrationUsers.CreateAsync(factory.Services, "project-reader",
        [
            new IntegrationUsers.Grant(ResourceType.Pipeline, seed.PipelineId, Permission.Write),
            new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Read),
            new IntegrationUsers.Grant(ResourceType.Server, seed.RunnerId, Permission.Admin)
        ], permissionKeys: [PermissionKeys.PipelineRun]);
        using var client = await IntegrationUsers.LoginClientAsync(factory, user.Username);

        var refused = await RunAsync(client, seed.PipelineId, Ungraded, gateOverride: true);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(0, await RunCountAsync(factory, seed.PipelineId));

        // Without the override the launch is the ordinary one, and the gate refuses it as before.
        var ordinary = await RunAsync(client, seed.PipelineId, Ungraded, gateOverride: false);
        Assert.Equal(HttpStatusCode.Created, ordinary.StatusCode);
        Assert.Equal(PipelineStatus.Failed, (await ReadRunAsync(factory, await RunIdAsync(ordinary))).Status);
    }

    private sealed record Seed(int ProjectId, int PipelineId, int RunnerId);

    private static Task<(int Id, string Username)> DeployerAsync(WebApplicationFactory<Program> factory, Seed seed) =>
        IntegrationUsers.CreateAsync(factory.Services, "deployer",
        [
            new IntegrationUsers.Grant(ResourceType.Pipeline, seed.PipelineId, Permission.Write),
            new IntegrationUsers.Grant(ResourceType.Project, seed.ProjectId, Permission.Write),
            new IntegrationUsers.Grant(ResourceType.Server, seed.RunnerId, Permission.Admin)
        ], permissionKeys: [PermissionKeys.PipelineRun]);

    private static Task<HttpResponseMessage> RunAsync(HttpClient client, int pipelineId, string version, bool gateOverride) =>
        client.PostAsJsonAsync($"/api/pipelines/{pipelineId}/run", new PipelineRunRequest
        {
            Parameters = new Dictionary<string, string> { ["candidateVersion"] = version },
            GateOverride = gateOverride
        }, IntegrationJsonOptions.Default, Ct);

    private static async Task<int> RunIdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<PipelineRunDto>(IntegrationJsonOptions.Default, Ct))!.Id;

    /// <summary>A deployment pipeline (it declares <c>candidateVersion</c>) of a project whose threshold is E, pinned
    /// to one online runner of the organization, and two releases of the project: one without a grade that keeps its
    /// artifact, one without a grade whose artifact is gone.</summary>
    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.Where(o => o.Slug == IntegrationUsers.DefaultOrganizationSlug)
            .Select(o => o.Id).FirstAsync(Ct);
        var suffix = Guid.NewGuid().ToString("N");
        var project = new Project
        {
            Name = $"redeploy-{suffix}",
            RepositoryUrl = $"https://git.example/{suffix}.git",
            OrganizationId = organizationId
        };
        var runner = new Server
        {
            Name = $"redeploy-runner-{suffix}",
            Hostname = $"redeploy-runner-{suffix}",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            LastHeartbeat = DateTime.UtcNow,
            OrganizationId = organizationId,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]"
        };
        db.AddRange(project, runner);
        await db.SaveChangesAsync(Ct);

        var pipeline = new Pipeline
        {
            Name = $"deploy-{suffix}",
            ProjectId = project.Id,
            YamlDefinition = $$"""
                name: deploy
                parameters:
                  - name: candidateVersion
                    type: string
                    required: true
                variables:
                  AETHEUS_DEPLOY_MINIMUM_GRADE: "E"
                  AETHEUS_CANDIDATE_VERSION: "$(candidateVersion)"
                stages:
                  - name: Deploy
                    agent: {{runner.Name}}
                    steps:
                      - name: Ship
                        shell: echo ship
                """
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(Ct);

        var publishing = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };
        db.PipelineRuns.Add(publishing);
        await db.SaveChangesAsync(Ct);
        var when = DateTime.UtcNow;
        db.Releases.Add(new Release
        {
            ProjectId = project.Id,
            Version = Ungraded,
            Status = ReleaseStatus.Published,
            DetectedAt = when,
            PublishedAt = when,
            Artifacts =
            [
                new PipelineArtifact
                {
                    PipelineId = pipeline.Id,
                    ProjectId = project.Id,
                    PipelineRunId = publishing.Id,
                    Name = "ApplicationPayload-artifacts",
                    FilePath = $"/artifacts/{suffix}",
                    CreatedAt = when,
                    RetentionExpiresAt = when.AddDays(30)
                }
            ]
        });
        db.Releases.Add(new Release
        {
            ProjectId = project.Id,
            Version = NoArtifact,
            Status = ReleaseStatus.Published,
            DetectedAt = when,
            PublishedAt = when
        });
        await db.SaveChangesAsync(Ct);
        // The publishing run is the seed's, not a launch: the counts below look at launches only.
        return new Seed(project.Id, pipeline.Id, runner.Id);
    }

    private static async Task<PipelineRun> ReadRunAsync(WebApplicationFactory<Program> factory, int runId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PipelineRuns.AsNoTracking().SingleAsync(run => run.Id == runId, Ct);
    }

    private static async Task<int> TaskCountAsync(WebApplicationFactory<Program> factory, int runId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tasks.AsNoTracking().CountAsync(task => task.PipelineRunId == runId, Ct);
    }

    /// <summary>The runs launched on the pipeline, the seeded publishing run (Success) left out.</summary>
    private static async Task<int> RunCountAsync(WebApplicationFactory<Program> factory, int pipelineId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PipelineRuns.AsNoTracking()
            .CountAsync(run => run.PipelineId == pipelineId && run.Status != PipelineStatus.Success, Ct);
    }

    /// <summary>The project's repository (git.example) never resolves: only the Git remote boundary is replaced,
    /// answering "no pipeline file" so the stored definition runs (as in PipelineApprovalDecisionHttpIntegrationTests).</summary>
    private static WebApplicationFactory<Program> WithoutRemotePipelineFile(AetheusWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IGitCliService, NoPipelineFileGitCli>()));

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
