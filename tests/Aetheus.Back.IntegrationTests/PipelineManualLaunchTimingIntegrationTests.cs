// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-7: after a click on "Exécuter" nothing visible happened for 10 to 15 seconds. The click makes four requests in a
/// row (the declared parameters, the preflight, the readiness verdict, the trigger). Measured on a real PostgreSQL
/// and a real internal Git repository for a pipeline that orchestrates three child pipelines (the shape of the
/// delivery chains), the trigger spent 77 percent of its time in the first stage dispatch, which launches the child
/// runs inside the request. A manual launch now answers once the run exists and dispatches its first stage after the
/// answer; these tests hold the answer while the dispatch is blocked, and hold the dispatch to its failure contract.
/// </summary>
public sealed class PipelineManualLaunchTimingIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheManualLaunch_AnswersWhileItsFirstStageDispatchIsStillBlocked_ThenDispatchesIt()
    {
        var planner = new BlockableDispatch();
        await using var factory = CreateFactory(planner);
        using var client = await LoginAsync(factory);
        var seeded = await PipelineLaunchScenario.SeedAsync(factory, client, followUp: false);
        var orchestrator = await SeedOrchestratorAsync(client, seeded.ProjectId, children: 3);

        planner.Block();
        var answer = client.PostAsJsonAsync($"/api/pipelines/{orchestrator.Id}/run", new PipelineRunRequest(), IntegrationJsonOptions.Default, Ct);
        var finished = await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(30), Ct));
        try
        {
            Assert.True(ReferenceEquals(answer, finished), "The launch waited for its first stage dispatch instead of answering.");
            using var response = await answer;
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var run = Assert.IsType<PipelineRunDto>(await response.Content.ReadFromJsonAsync<PipelineRunDto>(IntegrationJsonOptions.Default, Ct));
            Assert.Equal(PipelineStatus.Running, run.Status);
            Assert.True(planner.Started, "The first stage dispatch was never started.");
            // Answered while the dispatch is held: the three child runs it launches do not exist yet.
            Assert.Equal(0, await CountRunsOfChildrenAsync(factory, orchestrator.ChildNames));
        }
        finally
        {
            planner.Release();
        }

        await factory.Services.GetRequiredService<IPipelineDeferredFirstDispatch>().WhenIdleAsync();
        // The work moved, it was not dropped: the dispatch ran after the answer and launched every child.
        Assert.Equal(3, await CountRunsOfChildrenAsync(factory, orchestrator.ChildNames));
    }

    [Fact]
    public async Task AFirstStageDispatchThatFailsAfterTheAnswer_FailsTheRunWithItsReason()
    {
        var planner = new BlockableDispatch { FailWith = "the dispatch refused it" };
        await using var factory = CreateFactory(planner);
        using var client = await LoginAsync(factory);
        var seeded = await PipelineLaunchScenario.SeedAsync(factory, client, followUp: false);
        var orchestrator = await SeedOrchestratorAsync(client, seeded.ProjectId, children: 1);

        planner.Block();
        using var response = await client.PostAsJsonAsync($"/api/pipelines/{orchestrator.Id}/run", new PipelineRunRequest(), IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var answered = Assert.IsType<PipelineRunDto>(await response.Content.ReadFromJsonAsync<PipelineRunDto>(IntegrationJsonOptions.Default, Ct));
        Assert.Equal(PipelineStatus.Running, answered.Status);

        planner.Release();
        await factory.Services.GetRequiredService<IPipelineDeferredFirstDispatch>().WhenIdleAsync();

        var read = await client.GetAsync($"/api/pipelines/runs/{answered.Id}", Ct);
        var failed = Assert.IsType<PipelineRunDto>(await read.Content.ReadFromJsonAsync<PipelineRunDto>(IntegrationJsonOptions.Default, Ct));
        Assert.Equal(PipelineStatus.Failed, failed.Status);
        Assert.Contains(failed.Warnings, warning => warning.Contains("the dispatch refused it", StringComparison.Ordinal));
    }

    /// <summary>R-7 follow-up: the first dispatch runs after the answer, off the request, where no HttpContext names the
    /// person who clicked. The child runs it launches are still audited as that person, exactly as on the synchronous
    /// path (measured with the deferral off), never as "system".</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheChildRunsOfAClick_AreAuditedAsThePersonWhoClicked(bool deferred)
    {
        await using var factory = CreateFactory(new BlockableDispatch(), deferred);
        using var admin = await LoginAsync(factory);
        var seeded = await PipelineLaunchScenario.SeedAsync(factory, admin, followUp: false);
        var orchestrator = await SeedOrchestratorAsync(admin, seeded.ProjectId, children: 2);
        var adminName = await UsernameOfAsync(factory, IntegrationAuth.AdminEmail);

        using var response = await admin.PostAsJsonAsync($"/api/pipelines/{orchestrator.Id}/run", new PipelineRunRequest(), IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await WhenDispatchedAsync(factory, deferred);

        var actors = await TriggeredActorsOfChildrenAsync(factory, orchestrator.ChildNames);
        Assert.Equal(2, actors.Count);
        Assert.All(actors, actor => Assert.Equal(adminName, actor));
    }

    /// <summary>R-7 follow-up: a child launch is authorized against the child pipeline's owner (F-EXEC-1b), never against
    /// the person who clicked, on the synchronous path as on the deferred one. A person allowed to run the orchestrator and
    /// holding no right at all on its child pipelines gets the same outcome both ways: the children start, audited as
    /// that person, and no chained refusal is recorded.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APersonWithNoRightOnTheChildPipelines_GetsTheSameOutcomeDeferredOrNot(bool deferred)
    {
        await using var factory = CreateFactory(new BlockableDispatch(), deferred);
        using var admin = await LoginAsync(factory);
        var seeded = await PipelineLaunchScenario.SeedAsync(factory, admin, followUp: false);
        var orchestrator = await SeedOrchestratorAsync(admin, seeded.ProjectId, children: 2);
        // Exactly the rights the manual launch checks on the orchestrator (F-EXEC-1: Server.Admin on each of its
        // targets), and nothing on the child pipelines.
        var orchestratorTargets = await TargetServerIdsAsync(factory, orchestrator.Id);
        var launcher = await IntegrationUsers.CreateAsync(factory.Services, "r7-launcher",
            [
                new IntegrationUsers.Grant(ResourceType.Pipeline, orchestrator.Id, Permission.Read),
                .. orchestratorTargets.Select(serverId => new IntegrationUsers.Grant(ResourceType.Server, serverId, Permission.Admin))
            ],
            permissionKeys: [PermissionKeys.PipelineRun]);
        using var client = await IntegrationUsers.LoginClientAsync(factory, launcher.Username);

        using var response = await client.PostAsJsonAsync($"/api/pipelines/{orchestrator.Id}/run", new PipelineRunRequest(), IntegrationJsonOptions.Default, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await WhenDispatchedAsync(factory, deferred);

        var actors = await TriggeredActorsOfChildrenAsync(factory, orchestrator.ChildNames);
        Assert.Equal(2, actors.Count);
        Assert.All(actors, actor => Assert.Equal(launcher.Username, actor));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.AuditLogs.AsNoTracking().AnyAsync(
            log => log.Action == "BlockedUnauthorizedChainedRun" || log.Action == "BlockedUnownedChainedRun", Ct));
    }

    private static async Task WhenDispatchedAsync(WebApplicationFactory<Program> factory, bool deferred)
    {
        if (deferred) await factory.Services.GetRequiredService<IPipelineDeferredFirstDispatch>().WhenIdleAsync();
    }

    private static async Task<IReadOnlyCollection<int>> TargetServerIdsAsync(WebApplicationFactory<Program> factory, int pipelineId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var preparation = await scope.ServiceProvider.GetRequiredService<IPipelineRunService>().PrepareRunAsync(pipelineId, ct: Ct);
        return Assert.IsType<PipelineRunPreparation>(preparation).TargetServerIds;
    }

    private static async Task<string> UsernameOfAsync(WebApplicationFactory<Program> factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().Where(user => user.Email == email).Select(user => user.Username).SingleAsync(Ct);
    }

    /// <summary>The actor of the "Triggered" audit entry of every run of the child pipelines.</summary>
    private static async Task<List<string>> TriggeredActorsOfChildrenAsync(WebApplicationFactory<Program> factory, IReadOnlyList<string> childNames)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var childRunIds = await db.PipelineRuns.AsNoTracking()
            .Where(run => childNames.Contains(run.Pipeline!.Name)).Select(run => (int?)run.Id).ToListAsync(Ct);
        return await db.AuditLogs.AsNoTracking()
            .Where(log => log.Action == "Triggered" && log.EntityType == "PipelineRun" && childRunIds.Contains(log.EntityId))
            .Select(log => log.Username).ToListAsync(Ct);
    }

    /// <summary>A factory whose first stage dispatch can be held; <paramref name="deferred"/> false unregisters the
    /// deferred dispatch, which is the synchronous path every launch took before R-7 (the launcher's dependency is
    /// optional for that reason).</summary>
    private WebApplicationFactory<Program> CreateFactory(BlockableDispatch planner, bool deferred = true) =>
        new AetheusWebApplicationFactory(fixture.ConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IPipelineStageDispatchPlanner>(provider => new BlockingPlanner(
                ActivatorUtilities.CreateInstance<PipelineStageDispatchPlanner>(provider), planner));
            if (!deferred) services.RemoveAll<IPipelineDeferredFirstDispatch>();
        }));

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        IntegrationAuth.SetBearer(client, await IntegrationAuth.LoginAsAdminAsync(client, Ct));
        return client;
    }

    private sealed record Orchestrator(int Id, IReadOnlyList<string> ChildNames);

    private static async Task<Orchestrator> SeedOrchestratorAsync(HttpClient client, int projectId, int children)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new List<string>();
        for (var index = 1; index <= children; index++)
        {
            var name = $"orch-child-{index}-{suffix}";
            names.Add(name);
            await SaveAsync(client, projectId, name, $$"""
                name: {{name}}
                trigger: manual
                stages:
                  - name: Build
                    steps:
                      - name: Compile
                        shell: echo build
                """);
        }

        var steps = string.Join("\n", names.Select((name, index) =>
            $"      - name: child{index + 1}\n        type: trigger\n        pipeline: {name}"));
        var orchestratorName = $"orch-{suffix}";
        var orchestrator = await SaveAsync(client, projectId, orchestratorName,
            $"name: {orchestratorName}\ntrigger: manual\nstages:\n  - name: Orchestrate\n    steps:\n{steps}\n");
        return new Orchestrator(orchestrator.Id, names);
    }

    private static Task<PipelineDto> SaveAsync(HttpClient client, int projectId, string name, string yaml) =>
        PipelineLaunchScenario.PostAsync<PipelineDto>(client, "/api/pipelines", new CreatePipelineRequest
        {
            Name = name,
            Description = "saved through the API, committed to the project's repository",
            ProjectId = projectId,
            SourceBranch = "main",
            YamlDefinition = yaml
        });

    private static async Task<int> CountRunsOfChildrenAsync(WebApplicationFactory<Program> factory, IReadOnlyList<string> childNames)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PipelineRuns.AsNoTracking().CountAsync(run => childNames.Contains(run.Pipeline!.Name), Ct);
    }

    /// <summary>Holds the first stage dispatch of a launch until released, or makes it fail: the slow phase of the
    /// launch, made deterministic.</summary>
    private sealed class BlockableDispatch
    {
        private readonly object _gate = new();
        private TaskCompletionSource _released = NewGate();
        private bool _armed;
        private bool _started;

        public string? FailWith { get; init; }

        public bool Started
        {
            get
            {
                lock (_gate) return _started;
            }
        }

        public void Block()
        {
            lock (_gate) (_armed, _started, _released) = (true, false, NewGate());
        }

        public void Release() => _released.TrySetResult();

        /// <summary>Takes the gate for the first dispatch only: the dispatches the first one causes (a child run's own
        /// first stage) go through.</summary>
        public Task? TakeGate()
        {
            lock (_gate)
            {
                if (!_armed) return null;
                (_armed, _started) = (false, true);
                return _released.Task;
            }
        }

        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingPlanner(IPipelineStageDispatchPlanner inner, BlockableDispatch dispatch) : IPipelineStageDispatchPlanner
    {
        public Task<bool> CreateTasksForNextStageAsync(
            int runId, PipelineYamlDefinition definition, Dictionary<string, string> resolvedVars,
            List<PipelineStepRun> pendingSteps, int? organizationId, HashSet<string> secretKeys,
            IPipelineChildRunLauncher launcher, CancellationToken ct, int? ownerProjectId = null, PipelineRun? loadedRun = null) =>
            inner.CreateTasksForNextStageAsync(runId, definition, resolvedVars, pendingSteps, organizationId, secretKeys, launcher, ct, ownerProjectId, loadedRun);

        public async Task<bool> CreateTasksForNextStageAsync(
            int runId, PipelineYamlDefinition definition, Dictionary<string, string> resolvedVars,
            HashSet<string> secretKeys, IPipelineChildRunLauncher launcher, CancellationToken ct)
        {
            if (dispatch.TakeGate() is { } gate)
            {
                await gate.WaitAsync(ct);
                if (dispatch.FailWith is { } reason) throw new InvalidOperationException(reason);
            }

            return await inner.CreateTasksForNextStageAsync(runId, definition, resolvedVars, secretKeys, launcher, ct);
        }
    }
}
