// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.AppMonitoring.Ingest;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Recette R1-93 to R1-95: the server-side cost of the agent and telemetry hot paths the production
/// "slowest calls" export listed at 3 to 5 s (analysis report publication, task claim and completion,
/// heartbeat, OTLP logs and traces). Each call runs through the real services of the real host on a real
/// PostgreSQL, timed, with the SQL commands it sent counted, at several payload sizes. What this measures
/// is the server's own work without the network and without a loaded VPS; a cost that grows with the
/// payload by one query per item would show here as a command count that follows the size.
/// Set <c>AETHEUS_HOTPATH_BENCH_OUT</c> to append the result lines to a file.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ServerHotPathCostIntegrationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R1_93_AnalysisReportPublication_CostDoesNotGrowByOneQueryPerFinding()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var counter = new SqlCommandCounter();
        var pipelineId = await SeedAnalysisPipelineAsync(factory, Ct);
        var commandsBySize = new Dictionary<int, int>();
        // A first publication pays the JIT and EF model warm-up (measured at about 2 s); it is kept out
        // of the figures, as a long-running backend has paid it already.
        await PublishAsync(counter, factory, pipelineId, "warm-up", 500);

        foreach (var findings in new[] { 10, 500, 5_000 })
        {
            for (var round = 0; round < 3; round++)
            {
                // First publication: every finding is new. Second, on the next run: every finding exists.
                var salt = $"r{round}-{Guid.NewGuid():N}";
                foreach (var pass in new[] { "new", "known" })
                {
                    var (request, measure) = await PublishAsync(counter, factory, pipelineId, salt, findings);
                    Assert.Equal(findings, measure.Result.FindingCount);
                    Report($"analysis-report findings={findings} pass={pass} round={round} bytes={Encoding.UTF8.GetByteCount(request.ReportContent)}", measure);
                    if (pass == "known") commandsBySize[findings] = measure.Commands;
                }
            }
        }

        // Batched writes add a command per batch of rows, never one per finding.
        Assert.True(commandsBySize[5_000] < commandsBySize[10] + 5_000 / 20,
            $"5000 findings sent {commandsBySize[5_000]} SQL commands, 10 findings {commandsBySize[10]}.");
    }

    [Fact]
    public async Task R1_94_TaskClaimCompletionAndHeartbeat_ReportTheirCost()
    {
        await using var baseFactory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        // The run reads its stored definition: the repository answers "no pipeline file".
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IGitCliService, NoPipelineFileGitCli>()));
        using var counter = new SqlCommandCounter();
        var (serverId, pipelineId) = await SeedRunnerAndPipelineAsync(factory, Ct);
        const string session = "33333333333333333333333333333333";

        int runId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IPipelineRunService>().TriggerRunAsync(
                pipelineId, new Dictionary<string, string> { ["AETHEUS_SOURCE_COMMIT"] = new('a', 40) }, ct: Ct);
            runId = Assert.IsType<PipelineRunDto>(run).Id;
        }

        var completed = 0;
        for (var round = 0; round < 10; round++)
        {
            var claim = await counter.MeasureAsync(async () =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ITaskService>()
                    .GetPendingTasksAsync(serverId, 4, session, Ct);
            });
            Report($"task-claim round={round} claimed={claim.Result.Count}", claim);
            if (claim.Result.Count == 0) break;
            foreach (var task in claim.Result)
            {
                var lease = new AgentTaskLeaseRequest { AgentSessionId = task.AgentSessionId, AgentSessionFencingToken = task.AgentSessionFencingToken };
                await using (var scope = factory.Services.CreateAsyncScope())
                    Assert.True(await scope.ServiceProvider.GetRequiredService<ITaskService>().StartTaskAsync(task.Id, lease, Ct));
                var complete = await counter.MeasureAsync(async () =>
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
                Assert.True(complete.Result);
                completed++;
                Report($"task-complete task='{task.Name}'", complete);
            }
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var status = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PipelineRuns
                .Where(run => run.Id == runId).Select(run => run.Status).SingleAsync(Ct);
            Assert.Equal(PipelineStatus.Success, status);
        }
        // Clone, one task per step of the two stages, cleanup.
        Assert.Equal(4, completed);

        var beatCommands = new List<int>();
        for (var beat = 0; beat < 4; beat++)
        {
            var heartbeat = Heartbeat(session);
            var measure = await counter.MeasureAsync(async () =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IServerHeartbeatService>().ProcessHeartbeatAsync(serverId, heartbeat, Ct);
                return true;
            });
            beatCommands.Add(measure.Commands);
            Report($"heartbeat beat={beat} inventory={(beat == 0 ? "changed" : "unchanged")} containers={heartbeat.Docker.Containers.Count} services={heartbeat.Services.Count}", measure);
        }
        // An unchanged inventory is not rewritten (fingerprints): the repeated beats stay far cheaper.
        Assert.All(beatCommands.Skip(1), commands => Assert.True(commands < beatCommands[0], $"{commands} >= {beatCommands[0]}"));
    }

    [Fact]
    public async Task R1_95_OtlpLogsAndTraces_CostDoesNotGrowByOneQueryPerRecord()
    {
        await using var factory = new AetheusWebApplicationFactory(fixture.ConnectionString);
        using var counter = new SqlCommandCounter();
        var key = await SeedMonitoredAppAsync(factory, Ct);
        // The request runs in the test's execution context, so its SQL commands are counted.
        factory.Server.PreserveExecutionContext = true;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-aetheus-ingest-key", key);
        var logCommands = new Dictionary<int, int>();
        var traceCommands = new Dictionary<int, int>();

        foreach (var size in new[] { 10, 100, 1_000 })
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var logs = await PostAsync(counter, client, "logs", OtlpLogs(size));
                Report($"otlp-logs records={size} pass={pass}", logs);
                var traces = await PostAsync(counter, client, "traces", OtlpTraces(size));
                Report($"otlp-traces spans={size} pass={pass}", traces);
                logCommands[size] = logs.Commands;
                traceCommands[size] = traces.Commands;
            }
        }

        Assert.True(logCommands[1_000] <= logCommands[10] + 1_000 / 20,
            $"1000 log records sent {logCommands[1_000]} SQL commands, 10 records {logCommands[10]}.");
        Assert.True(traceCommands[1_000] <= traceCommands[10] + 1_000 / 20,
            $"1000 error spans sent {traceCommands[1_000]} SQL commands, 10 spans {traceCommands[10]}.");
    }

    private static async Task<(PublishAnalysisReportRequest Request, SqlCommandCounter.Measure<AnalysisReportDto> Measure)> PublishAsync(
        SqlCommandCounter counter, WebApplicationFactory<Program> factory, int pipelineId, string salt, int findings)
    {
        var (runId, artifactId) = await SeedRunAsync(factory, pipelineId, Ct);
        var request = SarifRequest(artifactId, findings, salt);
        var measure = await counter.MeasureAsync(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAnalysisService>().PublishReportAsync(runId, request, Ct);
        });
        return (request, measure);
    }

    private static async Task<SqlCommandCounter.Measure<HttpStatusCode>> PostAsync(
        SqlCommandCounter counter, HttpClient client, string signal, string json)
    {
        var measure = await counter.MeasureAsync(async () =>
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/api/ingest/otlp/v1/{signal}", content, Ct);
            return response.StatusCode;
        });
        Assert.Equal(HttpStatusCode.OK, measure.Result);
        return measure;
    }

    private static void Report<T>(string label, SqlCommandCounter.Measure<T> measure)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"hot-path {label} elapsed={measure.Elapsed.TotalMilliseconds:F0}ms sql={measure.Commands} sqlTime={measure.SqlTime.TotalMilliseconds:F0}ms");
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (System.Environment.GetEnvironmentVariable("AETHEUS_HOTPATH_BENCH_OUT") is { Length: > 0 } output)
            File.AppendAllText(output, line + System.Environment.NewLine);
    }

    private static PublishAnalysisReportRequest SarifRequest(int artifactId, int findings, string salt) => new()
    {
        ScannerKey = "opengrep",
        ScannerName = "Opengrep",
        ScannerVersion = "1.9.0",
        Category = AnalysisCategory.Sast,
        Status = AnalysisReportStatus.Failed,
        Format = AnalysisReportFormat.Sarif,
        StartedAt = DateTime.UtcNow.AddMinutes(-1),
        CompletedAt = DateTime.UtcNow,
        ReportPath = "opengrep.sarif",
        PipelineArtifactId = artifactId,
        ReportContent = JsonSerializer.Serialize(new
        {
            version = "2.1.0",
            runs = new[]
            {
                new
                {
                    tool = new { driver = new { name = "Opengrep", version = "1.9.0" } },
                    results = Enumerable.Range(0, findings).Select(index => new
                    {
                        ruleId = $"csharp.lang.security.rule-{index % 40}",
                        level = index % 3 == 0 ? "error" : "warning",
                        message = new { text = $"Finding {index}: user input reaches a sensitive sink." },
                        locations = new[]
                        {
                            new
                            {
                                physicalLocation = new
                                {
                                    artifactLocation = new { uri = $"src/{salt}/Module{index % 50}/File{index}.cs" },
                                    region = new { startLine = index % 400 + 1 }
                                }
                            }
                        }
                    })
                }
            }
        })
    };

    private static string OtlpLogs(int count)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        return JsonSerializer.Serialize(new
        {
            resourceLogs = new[]
            {
                new
                {
                    scopeLogs = new[]
                    {
                        new
                        {
                            scope = new { name = "Toto.Api.Orders" },
                            logRecords = Enumerable.Range(0, count).Select(index => new
                            {
                                timeUnixNano = (now - index * 1_000_000L).ToString(CultureInfo.InvariantCulture),
                                severityNumber = index % 10 == 0 ? 17 : 9,
                                severityText = index % 10 == 0 ? "Error" : "Information",
                                body = new { stringValue = $"Order {index} processed for customer {index % 97} in {index % 250} ms" },
                                attributes = new object[]
                                {
                                    new { key = "order.id", value = new { stringValue = $"ord-{index}" } },
                                    new { key = "exception.type", value = new { stringValue = index % 10 == 0 ? $"System.InvalidOperationException{index % 5}" : "" } }
                                }
                            })
                        }
                    }
                }
            }
        });
    }

    private static string OtlpTraces(int count)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
        return JsonSerializer.Serialize(new
        {
            resourceSpans = new[]
            {
                new
                {
                    scopeSpans = new[]
                    {
                        new
                        {
                            spans = Enumerable.Range(0, count).Select(index => new
                            {
                                name = $"GET /orders/{index % 20}",
                                endTimeUnixNano = (now - index * 1_000_000L).ToString(CultureInfo.InvariantCulture),
                                status = new { code = 2 },
                                events = new[]
                                {
                                    new
                                    {
                                        name = "exception",
                                        timeUnixNano = (now - index * 1_000_000L).ToString(CultureInfo.InvariantCulture),
                                        attributes = new[]
                                        {
                                            new { key = "exception.type", value = new { stringValue = $"Toto.OrderException{index % 30}" } }
                                        }
                                    }
                                }
                            })
                        }
                    }
                }
            }
        });
    }

    private static ServerHeartbeatDto Heartbeat(string session) => new()
    {
        AgentSessionId = session,
        AgentVersion = "1.3.9",
        AgentProtocolVersion = AgentProtocol.CurrentVersion,
        DockerAvailable = true,
        PipelineRunnerAvailable = true,
        CpuPercent = 12.5,
        MemoryUsedMb = 2048,
        MemoryTotalMb = 8192,
        Services = [.. Enumerable.Range(0, 60).Select(index => new ServiceInfoDto
        {
            Name = $"service-{index}",
            Type = ServiceType.Systemd,
            Status = "active",
            IsRunning = true
        })],
        Docker = new DockerDataDto
        {
            Containers = [.. Enumerable.Range(0, 40).Select(index => new DockerContainerDto
            {
                ContainerId = $"{index:x12}",
                Name = $"container-{index}",
                Image = $"registry.example/app-{index % 8}:1.0.{index}",
                State = "running",
                Status = "Up 3 hours"
            })]
        }
    };

    private static async Task<int> SeedAnalysisPipelineAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.Select(item => item.Id).FirstAsync(ct);
        var project = new Project { Name = $"hot-path-{Guid.NewGuid():N}", OrganizationId = organizationId, DefaultBranch = "main" };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline
        {
            Name = "Security",
            ProjectId = project.Id,
            YamlDefinition = "name: security\ntrigger: manual\nstages:\n  - name: Scan\n    steps:\n      - name: Sast\n        shell: echo scan\n"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return pipeline.Id;
    }

    private static async Task<(int RunId, int ArtifactId)> SeedRunAsync(WebApplicationFactory<Program> factory, int pipelineId, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = new PipelineRun
        {
            PipelineId = pipelineId,
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow,
            BranchName = "main",
            CommitHash = new string('b', 40)
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var artifact = new PipelineArtifact
        {
            PipelineId = pipelineId,
            PipelineRunId = run.Id,
            Name = "analysis-opengrep",
            FilePath = "opengrep.sarif"
        };
        db.PipelineArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        return (run.Id, artifact.Id);
    }

    private static async Task<(int ServerId, int PipelineId)> SeedRunnerAndPipelineAsync(
        WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.Select(item => item.Id).FirstAsync(ct);
        var suffix = Guid.NewGuid().ToString("N");
        var server = new Server
        {
            Name = $"hot-path-runner-{suffix}",
            Hostname = $"hot-path-runner-{suffix}",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            LastHeartbeat = DateTime.UtcNow,
            OrganizationId = organizationId,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\",\"shell.execute\"]"
        };
        var project = new Project
        {
            Name = $"hot-path-project-{suffix}",
            RepositoryUrl = $"https://git.example/{suffix}.git",
            OrganizationId = organizationId
        };
        db.AddRange(server, project);
        await db.SaveChangesAsync(ct);
        var pipeline = new Pipeline
        {
            Name = $"hot-path-pipeline-{suffix}",
            ProjectId = project.Id,
            YamlDefinition = "name: hot-path\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        shell: echo build\n  - name: Test\n    steps:\n      - name: Unit\n        shell: echo test\n"
        };
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(ct);
        return (server.Id, pipeline.Id);
    }

    private static async Task<string> SeedMonitoredAppAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        var key = $"hot-path-{Guid.NewGuid():N}";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var organizationId = await db.Organizations.Select(item => item.Id).FirstAsync(ct);
        var project = new Project { Name = $"hot-path-otlp-{Guid.NewGuid():N}", OrganizationId = organizationId };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        db.MonitoredApps.Add(new MonitoredApp
        {
            ProjectId = project.Id,
            Name = "toto-api",
            IngestKeyHash = scope.ServiceProvider.GetRequiredService<IngestKeyHasher>().Hash(key),
            IngestKeyExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync(ct);
        return key;
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
