// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-002 lot 10, gap 1: a run waiting on an approval is decided through
/// <c>POST /api/pipelines/approvals/{id}/decide</c> on the real host and PostgreSQL. The approver is a
/// non-admin holding Write on the pipeline only, so the decision goes through the real role check; a
/// reader (Read, no Write) is refused and nothing changes in the database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineApprovalDecisionHttpIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ApproverApproves_RunResumesAndDispatchesItsGatedStage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithoutRemotePipelineFile(baseFactory);
        var gate = await SeedWaitingRunAsync(factory);
        var approver = await IntegrationUsers.CreateAsync(factory.Services, "approver",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, gate.PipelineId, Permission.Write)]);
        using var client = await IntegrationUsers.LoginClientAsync(factory, approver.Username);

        var response = await client.PostAsJsonAsync($"/api/pipelines/approvals/{gate.ApprovalId}/decide",
            new ApprovalDecisionRequest { Decision = ApprovalStatus.Approved, Comments = "go" },
            IntegrationJsonOptions.Default, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decided = await response.Content.ReadFromJsonAsync<PipelineApprovalDto>(IntegrationJsonOptions.Default, ct);
        Assert.Equal(ApprovalStatus.Approved, decided!.Status);
        Assert.Equal(approver.Id, decided.ResolvedByUserId);

        var (approval, run) = await ReadAsync(factory, gate);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal(approver.Id, approval.ResolvedByUserId);
        Assert.Equal("go", approval.Comments);
        Assert.NotNull(approval.ResolvedAt);
        // The run proceeds: it runs again and its gated stage is now dispatched to the runner as tasks.
        Assert.Equal(PipelineStatus.Running, run.Status);
        Assert.True(await RunTaskCountAsync(factory, gate.RunId) > 0, "The approved run dispatched no task.");

        var runResponse = await client.GetFromJsonAsync<PipelineRunDto>(
            $"/api/pipelines/runs/{gate.RunId}", IntegrationJsonOptions.Default, ct);
        Assert.Equal(PipelineStatus.Running, runResponse!.Status);

        var audit = await DecisionAuditsAsync(factory, gate.ApprovalId);
        var entry = Assert.Single(audit);
        Assert.Equal(approver.Username, entry.Username);
        Assert.Contains("approved, run resumed", entry.Details);
    }

    [Fact]
    public async Task ApproverRejects_RunEndsFailedAndTheApprovalStaysRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithoutRemotePipelineFile(baseFactory);
        var gate = await SeedWaitingRunAsync(factory);
        var approver = await IntegrationUsers.CreateAsync(factory.Services, "rejecter",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, gate.PipelineId, Permission.Write)]);
        using var client = await IntegrationUsers.LoginClientAsync(factory, approver.Username);

        var response = await client.PostAsJsonAsync($"/api/pipelines/approvals/{gate.ApprovalId}/decide",
            new ApprovalDecisionRequest { Decision = ApprovalStatus.Rejected, Comments = "not today" },
            IntegrationJsonOptions.Default, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (approval, run) = await ReadAsync(factory, gate);
        Assert.Equal(ApprovalStatus.Rejected, approval.Status);
        Assert.Equal(approver.Id, approval.ResolvedByUserId);
        Assert.Equal("not today", approval.Comments);
        Assert.Equal(PipelineStatus.Failed, run.Status);
        Assert.Equal(0, await RunTaskCountAsync(factory, gate.RunId));

        // A second decision on the same approval is refused with its reason, the state stays as decided.
        var again = await client.PostAsJsonAsync($"/api/pipelines/approvals/{gate.ApprovalId}/decide",
            new ApprovalDecisionRequest { Decision = ApprovalStatus.Approved },
            IntegrationJsonOptions.Default, ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var (afterApproval, afterRun) = await ReadAsync(factory, gate);
        Assert.Equal(ApprovalStatus.Rejected, afterApproval.Status);
        Assert.Equal(PipelineStatus.Failed, afterRun.Status);
    }

    [Fact]
    public async Task ReaderWithoutApproverRight_Gets403_AndNothingChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = WithoutRemotePipelineFile(baseFactory);
        var gate = await SeedWaitingRunAsync(factory);
        var reader = await IntegrationUsers.CreateAsync(factory.Services, "approval-reader",
            [new IntegrationUsers.Grant(ResourceType.Pipeline, gate.PipelineId, Permission.Read)]);
        using var client = await IntegrationUsers.LoginClientAsync(factory, reader.Username);

        // The reader does see the pending approval: the refusal below is about the right, not visibility.
        var listed = await client.GetFromJsonAsync<List<PipelineApprovalDto>>(
            $"/api/pipelines/runs/{gate.RunId}/approvals", IntegrationJsonOptions.Default, ct);
        Assert.Equal(ApprovalStatus.Pending, Assert.Single(listed!).Status);

        foreach (var decision in new[] { ApprovalStatus.Approved, ApprovalStatus.Rejected })
        {
            var response = await client.PostAsJsonAsync($"/api/pipelines/approvals/{gate.ApprovalId}/decide",
                new ApprovalDecisionRequest { Decision = decision }, IntegrationJsonOptions.Default, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var (approval, run) = await ReadAsync(factory, gate);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Null(approval.ResolvedAt);
        Assert.Null(approval.ResolvedByUserId);
        Assert.Equal(PipelineStatus.WaitingForApproval, run.Status);
        Assert.Equal(0, await RunTaskCountAsync(factory, gate.RunId));
        Assert.Empty(await DecisionAuditsAsync(factory, gate.ApprovalId));
    }

    private sealed record Gate(int PipelineId, int RunId, int ApprovalId);

    /// <summary>
    /// A real run triggered through <see cref="IPipelineRunService"/>: its only stage deploys to an
    /// environment that requires approval, so the launch raises a pending environment approval and the
    /// run waits on it before any task exists. An online runner of the organization stands ready.
    /// </summary>
    private static async Task<Gate> SeedWaitingRunAsync(WebApplicationFactory<Program> factory)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.Where(o => o.Slug == "aetheus").Select(o => o.Id).FirstAsync(ct);
        var suffix = Guid.NewGuid().ToString("N");
        var project = new Project
        {
            Name = $"approval-{suffix}",
            RepositoryUrl = $"https://git.example/{suffix}.git",
            OrganizationId = organizationId
        };
        var runner = new Server
        {
            Name = $"approval-runner-{suffix}",
            Hostname = $"approval-runner-{suffix}",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            LastHeartbeat = DateTime.UtcNow,
            OrganizationId = organizationId,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]"
        };
        db.AddRange(project, runner);
        await db.SaveChangesAsync(ct);
        var environment = new Environment { Name = "prod", ProjectId = project.Id, RequireApproval = true, ApprovalTimeoutMinutes = 1440 };
        environment.Servers.Add(new EnvironmentServer { ServerId = runner.Id });
        db.Add(environment);
        var pipeline = new Pipeline
        {
            Name = $"gated-{suffix}",
            ProjectId = project.Id,
            YamlDefinition = "name: gated\nstages:\n  - name: Deploy\n    environment: prod\n    steps:\n      - name: Ship\n        shell: echo ship"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);

        var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunService>()
            .TriggerRunAsync(pipeline.Id, new Dictionary<string, string> { ["AETHEUS_SOURCE_COMMIT"] = new('a', 40) }, ct: ct);
        Assert.NotNull(run);
        var stored = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, ct);
        Assert.Equal(PipelineStatus.WaitingForApproval, stored.Status);
        var approval = await db.PipelineApprovals.AsNoTracking().SingleAsync(a => a.PipelineRunId == run.Id, ct);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Equal(ApprovalScope.Environment, approval.Scope);
        Assert.Equal(0, await db.Tasks.AsNoTracking().CountAsync(t => t.PipelineRunId == run.Id, ct));
        return new Gate(pipeline.Id, run.Id, approval.Id);
    }

    private static async Task<int> RunTaskCountAsync(WebApplicationFactory<Program> factory, int runId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Tasks.AsNoTracking().CountAsync(t => t.PipelineRunId == runId, TestContext.Current.CancellationToken);
    }

    private static async Task<(PipelineApproval Approval, PipelineRun Run)> ReadAsync(
        WebApplicationFactory<Program> factory, Gate gate)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var approval = await db.PipelineApprovals.AsNoTracking().SingleAsync(a => a.Id == gate.ApprovalId, ct);
        var run = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == gate.RunId, ct);
        return (approval, run);
    }

    private static async Task<List<AuditLog>> DecisionAuditsAsync(WebApplicationFactory<Program> factory, int approvalId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "ApprovalDecision" && a.EntityType == "PipelineApproval" && a.EntityId == approvalId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The project's repository (git.example) never resolves: like <c>PipelineTemplateRunIntegrationTests</c>,
    /// only the Git remote boundary is replaced, answering "no pipeline file" so the stored definition runs.
    /// </summary>
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
