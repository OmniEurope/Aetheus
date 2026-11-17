// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Monitoring;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R-10: the two calls of the production "slowest calls" export that were still unexplained, the dashboard
/// read and the task completion. Each runs through the real services on a real PostgreSQL; the SQL commands it sends
/// are counted (and kept, so a failing bound names the statements), and the count is bounded.
/// </summary>
public sealed class ProductionSlowCallsIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Session = "44444444444444444444444444444444";

    [Fact]
    public async Task R10_Dashboard_ReadsTheTilesInABoundedNumberOfCommands()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var counter = new SqlCommandCounter();
        var seeded = await SeedAsync(factory, Ct);
        // More roots than the tile lists (10): the count must not follow the number of runs.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PipelineRuns.AddRange(Enumerable.Range(0, 25).Select(index => new PipelineRun
            {
                PipelineId = seeded.PipelineId,
                Status = PipelineStatus.Success,
                StartedAt = DateTime.UtcNow.AddMinutes(-60 + index),
                CompletedAt = DateTime.UtcNow.AddMinutes(-59 + index),
                BranchName = "main",
                CommitHash = new string('d', 40)
            }));
            await db.SaveChangesAsync(Ct);
        }
        List<int> servers = [seeded.ServerId];
        List<int> projects = [seeded.ProjectId];
        List<int> pipelines = [seeded.PipelineId];
        // The first read pays the model warm-up; a long-running backend has paid it already.
        await DashboardAsync(counter, factory, servers, projects, pipelines);

        var measure = await DashboardAsync(counter, factory, servers, projects, pipelines);

        Dump("dashboard", measure.Statements);
        Assert.Equal(1, measure.Result.TotalServers);
        Assert.Equal(1, measure.Result.TotalProjects);
        Assert.NotEmpty(measure.Result.RecentRuns);
        Assert.True(measure.Commands <= DashboardCommandBudget,
            $"The dashboard sent {measure.Commands} SQL commands (budget {DashboardCommandBudget}):{System.Environment.NewLine}"
            + string.Join(System.Environment.NewLine, measure.Statements.Select(Shorten)));
    }

    [Fact]
    public async Task R10_TaskCompletion_AdvancesTheRunInABoundedNumberOfCommands()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        // The run reads its stored definition: the project has no repository behind its clone URL.
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<Aetheus.Back.Components.Git.IGitCliService, NoPipelineFileGitCli>()));
        using var counter = new SqlCommandCounter();
        var seeded = await SeedAsync(factory, Ct);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IPipelineRunService>().TriggerRunAsync(
                seeded.PipelineId, new Dictionary<string, string> { ["AETHEUS_SOURCE_COMMIT"] = new('a', 40) }, ct: Ct);

        var completions = new List<SqlCommandCounter.Measure<bool>>();
        var names = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            List<PendingTaskDto> claimed;
            await using (var scope = factory.Services.CreateAsyncScope())
                claimed = [.. await scope.ServiceProvider.GetRequiredService<ITaskService>()
                    .GetPendingTasksAsync(seeded.ServerId, 4, Session, Ct)];
            if (claimed.Count == 0) break;
            foreach (var task in claimed)
            {
                var lease = new AgentTaskLeaseRequest { AgentSessionId = task.AgentSessionId, AgentSessionFencingToken = task.AgentSessionFencingToken };
                await using (var scope = factory.Services.CreateAsyncScope())
                    Assert.True(await scope.ServiceProvider.GetRequiredService<ITaskService>().StartTaskAsync(task.Id, lease, Ct));
                var measure = await counter.MeasureAsync(async () =>
                {
                    await using var scope = factory.Services.CreateAsyncScope();
                    return await scope.ServiceProvider.GetRequiredService<ITaskService>().CompleteTaskAsync(task.Id, new TaskResultDto
                    {
                        TaskId = task.Id,
                        Status = TaskExecutionStatus.Success,
                        ExitCode = 0,
                        AgentSessionId = task.AgentSessionId,
                        AgentSessionFencingToken = task.AgentSessionFencingToken
                    }, Ct);
                });
                Assert.True(measure.Result);
                Dump($"complete {task.Name}", measure.Statements);
                completions.Add(measure);
                names.Add(task.Name);
            }
        }

        Assert.Equal(4, completions.Count);
        var worst = completions.MaxBy(measure => measure.Commands)!;
        Assert.True(worst.Commands <= CompletionCommandBudget,
            $"A task completion sent {worst.Commands} SQL commands (budget {CompletionCommandBudget}):{System.Environment.NewLine}"
            + string.Join(System.Environment.NewLine, worst.Statements.Select(Shorten)));

        // Each kind of completion has its own measured count, so one that grows back is caught even when it is not the worst.
        for (var index = 0; index < completions.Count; index++)
        {
            Assert.True(CompletionCommandBudgets.TryGetValue(names[index], out var budget), $"No command budget for the completion of '{names[index]}'.");
            Assert.True(completions[index].Commands <= budget,
                $"The completion of '{names[index]}' sent {completions[index].Commands} SQL commands (budget {budget}):{System.Environment.NewLine}"
                + string.Join(System.Environment.NewLine, completions[index].Statements.Select(Shorten)));
        }
    }

    /// <summary>
    /// The completion that ends a run asked whether a declared artifact failed to be collected twice for the same run: once
    /// to choose the status it requests, once more when the terminal status is checked against that fact. The scheduler
    /// reads it once and hands the answer to the completion.
    /// </summary>
    [Fact]
    public async Task R10_RunCompletion_ReadsTheFailedArtifactCollectionOnceForTheRun()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<Aetheus.Back.Components.Git.IGitCliService, NoPipelineFileGitCli>()));
        using var counter = new SqlCommandCounter();
        var seeded = await SeedAsync(factory, Ct);
        int runId;
        await using (var scope = factory.Services.CreateAsyncScope())
            runId = (await scope.ServiceProvider.GetRequiredService<IPipelineRunService>().TriggerRunAsync(
                seeded.PipelineId, new Dictionary<string, string> { ["AETHEUS_SOURCE_COMMIT"] = new('a', 40) }, ct: Ct))!.Id;

        var perCompletion = new List<(string Name, int Reads)>();
        for (var round = 0; round < 10; round++)
        {
            List<PendingTaskDto> claimed;
            await using (var scope = factory.Services.CreateAsyncScope())
                claimed = [.. await scope.ServiceProvider.GetRequiredService<ITaskService>()
                    .GetPendingTasksAsync(seeded.ServerId, 4, Session, Ct)];
            if (claimed.Count == 0) break;
            foreach (var task in claimed)
            {
                var lease = new AgentTaskLeaseRequest { AgentSessionId = task.AgentSessionId, AgentSessionFencingToken = task.AgentSessionFencingToken };
                await using (var scope = factory.Services.CreateAsyncScope())
                    Assert.True(await scope.ServiceProvider.GetRequiredService<ITaskService>().StartTaskAsync(task.Id, lease, Ct));
                var measure = await counter.MeasureAsync(async () =>
                {
                    await using var scope = factory.Services.CreateAsyncScope();
                    return await scope.ServiceProvider.GetRequiredService<ITaskService>().CompleteTaskAsync(task.Id, new TaskResultDto
                    {
                        TaskId = task.Id,
                        Status = TaskExecutionStatus.Success,
                        ExitCode = 0,
                        AgentSessionId = task.AgentSessionId,
                        AgentSessionFencingToken = task.AgentSessionFencingToken
                    }, Ct);
                });
                Assert.True(measure.Result);
                perCompletion.Add((task.Name, measure.Statements.Count(IsFailedArtifactCollectionRead)));
            }
        }

        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.Equal(PipelineStatus.Success,
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().PipelineRuns.AsNoTracking()
                    .Where(run => run.Id == runId).Select(run => run.Status).SingleAsync(Ct));
        Assert.Equal(4, perCompletion.Count);
        Assert.All(perCompletion, completion => Assert.True(completion.Reads <= 1,
            $"The completion of '{completion.Name}' read the failed artifact collection of the run {completion.Reads} times."));
        // The completion that ends the run still reads it, once, to choose the status it requests.
        Assert.Contains(perCompletion, completion => completion.Reads == 1);
    }

    /// <summary>The <c>HasFailedArtifactCollectionAsync</c> statement: an <c>EXISTS</c> over the run's tasks that
    /// looks at the step each collection belongs to.</summary>
    private static bool IsFailedArtifactCollectionRead(string statement) =>
        statement.Contains("EXISTS", StringComparison.Ordinal)
        && statement.Contains("FROM \"Tasks\"", StringComparison.Ordinal)
        && statement.Contains("\"ContinueOnError\"", StringComparison.Ordinal);

    // Measured 12 on a fixed set of reads (servers, pending tasks, running count, run groups, projects); the budget leaves
    // room for the conditional run-grade and release lookups, never for a query per row.
    private const int DashboardCommandBudget = 14;
    // Measured 36 (clone), 24, 34 and 34 before the second pass of R-10; then 29 (clone), 21, 30 and 31 after it; now 28 (clone), 19,
    // 26 and 27 after the third (the approvals read only for a definition with a confirmation, one read for the completed and the
    // terminal stages, one for the failed steps at the end of the run, one read of the dependency update a completed run belongs to,
    // the run and its affinity handed to the system stages). The clone completion is the worst case.
    private const int CompletionCommandBudget = 28;

    private static readonly Dictionary<string, int> CompletionCommandBudgets = new()
    {
        ["Clone Repository"] = 28,
        ["Compile"] = 19,
        ["Unit"] = 26,
        ["Cleanup"] = 27
    };

    private static Task<SqlCommandCounter.Measure<DashboardOverviewDto>> DashboardAsync(
        SqlCommandCounter counter, WebApplicationFactory<Program> factory,
        List<int> servers, List<int> projects, List<int> pipelines) =>
        counter.MeasureAsync(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IMonitoringService>()
                .GetDashboardAsync(servers, projects, pipelines, Ct);
        });

    private static string Shorten(string statement)
    {
        var text = statement.ReplaceLineEndings(" ");
        return text.Length <= 200 ? text : text[..200];
    }

    private static void Dump(string label, IReadOnlyList<string> statements)
    {
        if (System.Environment.GetEnvironmentVariable("AETHEUS_HOTPATH_BENCH_OUT") is not { Length: > 0 } output) return;
        File.AppendAllLines(output, [$"== {label}: {statements.Count}", .. statements.Select(Shorten)]);
    }

    private static async Task<(int ServerId, int ProjectId, int PipelineId)> SeedAsync(
        WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.OrderBy(item => item.Id).Select(item => item.Id).FirstAsync(ct);
        var suffix = Guid.NewGuid().ToString("N");
        var server = new Server
        {
            Name = $"slow-calls-runner-{suffix}",
            Hostname = $"slow-calls-runner-{suffix}",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            LastHeartbeat = DateTime.UtcNow,
            OrganizationId = organizationId,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]"
        };
        var project = new Project
        {
            Name = $"slow-calls-project-{suffix}",
            RepositoryUrl = $"http://localhost:5300/git/1/{suffix}.git",
            OrganizationId = organizationId
        };
        db.AddRange(server, project);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline
        {
            Name = $"slow-calls-pipeline-{suffix}",
            ProjectId = project.Id,
            YamlDefinition = "name: slow-calls\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        shell: echo build\n  - name: Test\n    steps:\n      - name: Unit\n        shell: echo test\n"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        db.PipelineRuns.Add(new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4),
            BranchName = "main",
            CommitHash = new string('c', 40)
        });
        await db.SaveChangesAsync(ct);
        return (server.Id, project.Id, pipeline.Id);
    }

    private sealed class NoPipelineFileGitCli : Aetheus.Back.Components.Git.IGitCliService
    {
        public Task<List<(string BranchName, string Version)>> ListReleaseBranchesAsync(string repositoryUrl, CancellationToken ct = default) =>
            Task.FromResult(new List<(string BranchName, string Version)>());
    }
}
