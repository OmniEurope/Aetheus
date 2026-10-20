// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

public class PipelineRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public PipelineRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Aetheus.Back.Components.Pipelines.PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
    }

    // --- GetPipelinesPagedAsync ---

    [Fact]
    public async Task GetPipelinesPagedAsync_ReturnsPagedResults()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "Alpha", YamlDefinition = "y" },
            new Pipeline { Name = "Beta", YamlDefinition = "y" },
            new Pipeline { Name = "Gamma", YamlDefinition = "y" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetPipelinesPagedAsync(null, null, 1, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        Assert.Equal(2, items.Count);
        Assert.Equal("Alpha", items[0].Name);
        Assert.Equal("Beta", items[1].Name);
    }

    [Fact]
    public async Task GetPipelinesPagedAsync_WithSearch_Filters()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "Deploy API", YamlDefinition = "y" },
            new Pipeline { Name = "Build UI", YamlDefinition = "y" }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetPipelinesPagedAsync("Deploy", null, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("Deploy API", items[0].Name);
    }

    [Fact]
    public async Task GetPipelinesPagedAsync_WithTriggerFilter_Filters()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "Manual", YamlDefinition = "y", TriggerType = PipelineTriggerType.Manual },
            new Pipeline { Name = "Webhook", YamlDefinition = "y", TriggerType = PipelineTriggerType.Webhook }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetPipelinesPagedAsync(null, PipelineTriggerType.Webhook, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, total);
        Assert.Equal("Webhook", items[0].Name);
    }

    [Fact]
    public async Task GetPipelinesPagedAsync_Page2_ReturnsCorrectSlice()
    {
        for (var i = 1; i <= 5; i++)
            _db.Pipelines.Add(new Pipeline { Name = $"P{i:D2}", YamlDefinition = "y" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetPipelinesPagedAsync(null, null, 2, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(5, total);
        Assert.Equal(2, items.Count);
        Assert.Equal("P03", items[0].Name);
    }

    // --- GetPipelineWithRunsAsync ---

    [Fact]
    public async Task GetPipelineWithRunsAsync_Found_IncludesRuns()
    {
        var pipeline = new Pipeline { Name = "CI", YamlDefinition = "y" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow.AddHours(-2) },
            new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetPipelineWithRunsAsync(pipeline.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result.Runs.Count);
    }

    [Fact]
    public async Task GetPipelineWithRunsAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.GetPipelineWithRunsAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    // --- FindPipelineAsync ---

    [Fact]
    public async Task FindPipelineAsync_Found_ReturnsPipeline()
    {
        var p = new Pipeline { Name = "Test", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindPipelineAsync(p.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("Test", result.Name);
    }

    [Fact]
    public async Task FindPipelineAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.FindPipelineAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    // --- AddPipelineAsync ---

    [Fact]
    public async Task AddPipelineAsync_Persists()
    {
        var p = new Pipeline { Name = "New", YamlDefinition = "stages: []" };
        await _repo.AddPipelineAsync(p, ct: TestContext.Current.CancellationToken);

        Assert.True(p.Id > 0);
        Assert.Equal(1, await _db.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- RemovePipelineAsync ---

    [Fact]
    public async Task RemovePipelineAsync_Removes()
    {
        var p = new Pipeline { Name = "Del", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RemovePipelineAsync(p, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, await _db.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- AddPipelineRunAsync ---

    [Fact]
    public async Task AddPipelineRunAsync_Persists()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        await _repo.AddPipelineRunAsync(run, ct: TestContext.Current.CancellationToken);

        Assert.True(run.Id > 0);
    }

    [Fact]
    public async Task GetOrAddPipelineRunAsync_SameIdempotencyKey_ReturnsExistingRun()
    {
        var pipeline = new Pipeline { Name = "deploy", YamlDefinition = "name: deploy\nstages: []" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var first = new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow,
            IdempotencyKey = "request-42"
        };

        var created = await _repo.GetOrAddPipelineRunAsync(
            first, TestContext.Current.CancellationToken);
        var duplicate = await _repo.GetOrAddPipelineRunAsync(new PipelineRun
        {
            PipelineId = pipeline.Id,
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow.AddSeconds(1),
            IdempotencyKey = "request-42"
        }, TestContext.Current.CancellationToken);

        Assert.True(created.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(created.Run.Id, duplicate.Run.Id);
        Assert.Single(await _db.PipelineRuns.ToListAsync(TestContext.Current.CancellationToken));
    }

    // --- TrackPipelineStepRun ---

    [Fact]
    public async Task TrackPipelineStepRun_AddsToContext()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var stepRun = new PipelineStepRun { PipelineRunId = run.Id, StepName = "build", StageName = "build", Order = 1, Status = TaskExecutionStatus.Pending };
        _repo.TrackPipelineStepRun(stepRun);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, await _db.PipelineStepRuns.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- AppendRunWarningsAsync (regression: no-server failure must persist a visible reason) ---

    [Fact]
    public async Task AppendRunWarningsAsync_PersistsReasonToStore()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AppendRunWarningsAsync(run.Id, ["Stage 'deploy': no online server matches agent 'linux-01'."], ct: TestContext.Current.CancellationToken);

        // Drop the identity map so the assertion reflects what is actually persisted in the store,
        // not an in-memory mutation. The previous AsNoTracking-mutate bug would fail here.
        _db.ChangeTracker.Clear();
        var reloaded = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded.WarningsJson);
        // Deserialize (as MapRunToDto does) so the assertion is independent of JSON unicode escaping.
        var warnings = JsonSerializer.Deserialize<List<string>>(reloaded.WarningsJson!);
        Assert.NotNull(warnings);
        Assert.Contains("Stage 'deploy': no online server matches agent 'linux-01'.", warnings);
    }

    [Fact]
    public async Task AppendRunWarningsAsync_MergesWithExistingWarnings()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun
        {
            PipelineId = p.Id,
            Status = PipelineStatus.Running,
            StartedAt = DateTime.UtcNow,
            WarningsJson = """["Vault 'prod' not found."]"""
        };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AppendRunWarningsAsync(run.Id, ["Stage 'x': no online server."], ct: TestContext.Current.CancellationToken);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        var warnings = JsonSerializer.Deserialize<List<string>>(reloaded.WarningsJson!);
        Assert.NotNull(warnings);
        Assert.Contains("Vault 'prod' not found.", warnings);
        Assert.Contains("Stage 'x': no online server.", warnings);
    }

    [Fact]
    public async Task AppendRunWarningsAsync_EmptyCollection_IsNoOp()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AppendRunWarningsAsync(run.Id, [], ct: TestContext.Current.CancellationToken);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.WarningsJson);
    }

    // --- ResetFailedStepRunsAsync (partial re-run) ---

    [Fact]
    public async Task ResetFailedStepRunsAsync_ResetsFailedStepsAndReopensRun()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun
        {
            PipelineId = p.Id,
            Status = PipelineStatus.Failed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            AdditionalVariablesJson =
                $"{{\"{PipelineRunService.CancellationRequestedVariable}\":\"true\",\"KEEP\":\"value\"}}"
        };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "a", StepName = "ok", Order = 0, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "a", StepName = "ko", Order = 1, Status = TaskExecutionStatus.Failed, ExitCode = 1, FailureCode = "ToolError", FailureReason = "failed", StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "b", StepName = "downstream", Order = 2, Status = TaskExecutionStatus.Cancelled, CompletedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var count = await _repo.ResetFailedStepRunsAsync(run.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        _db.ChangeTracker.Clear();
        var reloadedRun = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(reloadedRun);
        Assert.Equal(PipelineStatus.Running, reloadedRun.Status);
        Assert.Null(reloadedRun.CompletedAt);
        var variables = PipelineRunHelpers.DeserializeResolvedVariables(reloadedRun.AdditionalVariablesJson);
        Assert.False(variables.ContainsKey(PipelineRunService.CancellationRequestedVariable));
        Assert.Equal("value", variables["KEEP"]);
        var steps = await _db.PipelineStepRuns.AsNoTracking().Where(s => s.PipelineRunId == run.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Success, steps.Single(s => s.StepName == "ok").Status);
        var retried = steps.Single(s => s.StepName == "ko");
        Assert.Equal(TaskExecutionStatus.Pending, retried.Status);
        Assert.Null(retried.ExitCode);
        Assert.Null(retried.FailureCode);
        Assert.Null(retried.FailureReason);
        Assert.Null(retried.StartedAt);
        Assert.Null(retried.CompletedAt);
        var downstream = steps.Single(s => s.StepName == "downstream");
        Assert.Equal(TaskExecutionStatus.Pending, downstream.Status);
        Assert.Null(downstream.CompletedAt);
    }

    /// <summary>
    /// Production run 2240 failed its retry twice on "cannot open
    /// deploy/scripts/finalize-fast-release-transaction.sh". Its agent had restarted, taking the
    /// workspace with it, and nothing put the repository back: <c>checkout: true</c> stopped cloning
    /// (the double-clone fix), only System:Prepare clones, and System:Prepare was Success so the retry
    /// never touched it. A retry has to replay the preparation, or it retries into an empty directory.
    /// </summary>
    [Fact]
    public async Task ResetFailedStepRunsAsync_ReplaysThePreparationThatPutsTheRepositoryInTheWorkspace()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Failed, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StageName = PipelineRunService.SystemPrepareStage, StepName = "Clone Repository", Order = 0, IsSystem = true, Status = TaskExecutionStatus.Success, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "a", StepName = "ok", Order = 1, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StageName = "b", StepName = "ko", Order = 2, Status = TaskExecutionStatus.Failed, ExitCode = 2, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var count = await _repo.ResetFailedStepRunsAsync(run.Id, ct: TestContext.Current.CancellationToken);

        // The count still answers "how many failed steps am I retrying", so the caller's
        // "nothing to retry" refusal keeps its meaning: the preparation is not a retried step.
        Assert.Equal(1, count);
        _db.ChangeTracker.Clear();
        var steps = await _db.PipelineStepRuns.AsNoTracking()
            .Where(s => s.PipelineRunId == run.Id)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        var prepare = steps.Single(s => s.StageName == PipelineRunService.SystemPrepareStage);
        Assert.Equal(TaskExecutionStatus.Pending, prepare.Status);
        Assert.Null(prepare.StartedAt);
        Assert.Null(prepare.CompletedAt);
        Assert.Equal(TaskExecutionStatus.Pending, steps.Single(s => s.StepName == "ko").Status);
        // A step that already succeeded is not replayed just because the preparation is.
        Assert.Equal(TaskExecutionStatus.Success, steps.Single(s => s.StepName == "ok").Status);
    }

    /// <summary>
    /// The preparation is replayed with a retry, never on its own: a run with nothing to retry stays
    /// closed, and its workspace is not rebuilt for no reason.
    /// </summary>
    [Fact]
    public async Task ResetFailedStepRunsAsync_LeavesThePreparationAloneWhenThereIsNothingToRetry()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Failed, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, StageName = PipelineRunService.SystemPrepareStage, StepName = "Clone Repository", Order = 0, IsSystem = true, Status = TaskExecutionStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var count = await _repo.ResetFailedStepRunsAsync(run.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        _db.ChangeTracker.Clear();
        var prepare = await _db.PipelineStepRuns.AsNoTracking()
            .SingleAsync(s => s.PipelineRunId == run.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TaskExecutionStatus.Success, prepare.Status);
    }

    [Fact]
    public async Task ResetFailedStepRunsAsync_NoFailedSteps_ReturnsZeroAndNoChange()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Failed, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, StageName = "a", StepName = "ok", Order = 0, Status = TaskExecutionStatus.Success });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var count = await _repo.ResetFailedStepRunsAsync(run.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        _db.ChangeTracker.Clear();
        var reloadedRun = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.Equal(PipelineStatus.Failed, reloadedRun!.Status);
    }

    // --- GetPendingStepRunsAsync ---

    [Fact]
    public async Task GetPendingStepRunsAsync_ReturnsPendingOnly()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s1", StageName = "build", Order = 1, Status = TaskExecutionStatus.Pending },
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s2", StageName = "build", Order = 2, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s3", StageName = "build", Order = 3, Status = TaskExecutionStatus.Pending }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetPendingStepRunsAsync(run.Id, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("s1", result[0].StepName);
        Assert.Equal("s3", result[1].StepName);
    }

    // --- FindOnlineServerByAgentAsync ---

    [Fact]
    public async Task FindOnlineServerByAgent_ByName_ReturnsServer()
    {
        // Item #11: PipelineRunnerEnabled is secure-by-default OFF; tests that expect a match
        // must opt the server in.
        _db.Servers.Add(new Server
        {
            Name = "build-01",
            Hostname = "h",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\"]"
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindOnlineServerByAgentAsync("build-01", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindOnlineServerByAgent_OfflineServer_ReturnsNull()
    {
        _db.Servers.Add(new Server { Name = "build-01", Hostname = "h", Status = ServerStatus.Offline });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindOnlineServerByAgentAsync("build-01", ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    // --- OS typing (Phase 8) ---

    [Fact]
    public async Task FindOnlineServerByAgent_OsMismatch_ReturnsNull()
    {
        _db.Servers.Add(new Server
        {
            Name = "linux-01",
            Hostname = "h",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\"]",
            OsType = OsType.Linux
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Requesting a Windows runner must NOT resolve the Linux server.
        var result = await _repo.FindOnlineServerByAgentAsync("linux-01", OsType.Windows, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindOnlineServerByAgent_OsMatch_ReturnsServer()
    {
        _db.Servers.Add(new Server
        {
            Name = "linux-01",
            Hostname = "h",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\"]",
            OsType = OsType.Linux
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindOnlineServerByAgentAsync("linux-01", OsType.Linux, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindAnyOnlineRunner_OsFilter_PicksMatchingOsOnly()
    {
        _db.Servers.AddRange(
            new Server { Name = "lin", Hostname = "h1", Status = ServerStatus.Online, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"pipeline.build\"]", OsType = OsType.Linux },
            new Server { Name = "win", Hostname = "h2", Status = ServerStatus.Online, PipelineRunnerEnabled = true, AgentProtocolVersion = AgentProtocol.CurrentVersion, AgentCapabilitiesJson = "[\"pipeline.build\"]", OsType = OsType.Windows });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var win = await _repo.FindAnyOnlineRunnerAsync(null, OsType.Windows, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(win);
        Assert.Equal("win", win!.Name);
    }

    [Fact]
    public async Task FindAnyOnlineRunner_OsFilter_NoCompatibleServer_ReturnsNull()
    {
        _db.Servers.Add(new Server
        {
            Name = "lin",
            Hostname = "h1",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\"]",
            OsType = OsType.Linux
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Only a Linux runner is online - a Windows-typed stage finds nothing.
        var result = await _repo.FindAnyOnlineRunnerAsync(null, OsType.Windows, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task FindAnyOnlineRunner_UnknownOs_NoConstraint_ReturnsAny()
    {
        _db.Servers.Add(new Server
        {
            Name = "lin",
            Hostname = "h1",
            Status = ServerStatus.Online,
            PipelineRunnerEnabled = true,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = "[\"pipeline.build\"]",
            OsType = OsType.Linux
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // OsType.Unknown means "no constraint" - pre-typing behavior preserved.
        var result = await _repo.FindAnyOnlineRunnerAsync(null, OsType.Unknown, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    // --- TrackTask ---

    [Fact]
    public async Task TrackTask_AddsToContext()
    {
        var s = new Server { Name = "srv", Hostname = "h" };
        _db.Servers.Add(s);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var task = new ServerTask { ServerId = s.Id, Name = "t", Command = "echo" };
        _repo.TrackTask(task);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, await _db.Tasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- GetRunsAsync ---

    [Fact]
    public async Task GetRunsAsync_ReturnsOrderedByStartedAt()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineRuns.AddRange(
            new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow.AddHours(-2) },
            new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRunsAsync(p.Id, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].StartedAt > result[1].StartedAt);
    }

    // --- GetRunDetailAsync ---

    [Fact]
    public async Task R484_TheRunDetail_LeavesItsTestRowsOut_AndTheSummaryCountsThemByOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(ct);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };
        var other = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.AddRange(run, other);
        await _db.SaveChangesAsync(ct);
        _db.TestResults.AddRange(
            new TestResult { PipelineRunId = run.Id, TestName = "a", Outcome = TestOutcome.Passed, DurationMs = 10 },
            new TestResult { PipelineRunId = run.Id, TestName = "b", Outcome = TestOutcome.Passed, DurationMs = 5 },
            new TestResult { PipelineRunId = run.Id, TestName = "c", Outcome = TestOutcome.Failed, DurationMs = 2.5 },
            new TestResult { PipelineRunId = run.Id, TestName = "d", Outcome = TestOutcome.Skipped },
            new TestResult { PipelineRunId = other.Id, TestName = "z", Outcome = TestOutcome.Error, DurationMs = 99 });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        var detail = await _repo.GetRunDetailAsync(run.Id, ct);
        var summary = await _repo.GetTestResultSummaryAsync(run.Id, ct);

        Assert.NotNull(detail);
        Assert.Empty(detail.TestResults);
        Assert.NotNull(summary);
        Assert.Equal((4, 2, 1, 1, 0, 17.5), (summary.TotalTests, summary.Passed, summary.Failed, summary.Skipped, summary.Errors, summary.TotalDurationMs));
        Assert.Null(await _repo.GetTestResultSummaryAsync(9999, ct));
    }

    /// <summary>The detail loads no coverage, lint, metric or artifact rows; their figures come
    /// from the database: the canonical coverage report without its per-file list, the last lint
    /// report, the metrics and the artifacts with the run's branch and commit.</summary>
    [Fact]
    public async Task R484_TheRunDetail_LeavesItsResultRowsOut_AndTheSummariesComeFromTheDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(ct);
        var run = new PipelineRun { PipelineId = p.Id, StartedAt = DateTime.UtcNow, BranchName = "develop", CommitHash = "abc123" };
        var other = new PipelineRun { PipelineId = p.Id, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.AddRange(run, other);
        await _db.SaveChangesAsync(ct);
        var now = DateTime.UtcNow;
        _db.CoverageResults.AddRange(
            new CoverageResult { PipelineRunId = run.Id, LineRate = 0.5, LinesCovered = 5, LinesValid = 10, FilesJson = "[{\"File\":\"a.cs\"}]", CreatedAt = now },
            new CoverageResult { PipelineRunId = run.Id, LineRate = 0.8, LinesCovered = 80, LinesValid = 100, FilesJson = "[{\"File\":\"b.cs\"}]", CreatedAt = now.AddMinutes(-1) },
            new CoverageResult { PipelineRunId = other.Id, LinesValid = 1000, CreatedAt = now });
        _db.LintResults.AddRange(
            new LintResult { PipelineRunId = run.Id, Tool = "old", ErrorCount = 3, CreatedAt = now.AddMinutes(-5) },
            new LintResult { PipelineRunId = run.Id, Tool = "new", WarningCount = 2, CreatedAt = now });
        _db.RunMetrics.Add(new RunMetric { PipelineRunId = run.Id, Key = "loc.total", Value = 42, StepName = "build", CreatedAt = now });
        _db.PipelineArtifacts.Add(new PipelineArtifact { PipelineRunId = run.Id, PipelineId = p.Id, Name = "drop", FilePath = "1/drop.zip", SizeBytes = 7, Sha256 = new string('a', 64), CreatedAt = now });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        var detail = await _repo.GetRunDetailAsync(run.Id, ct);
        Assert.NotNull(detail);
        Assert.Empty(detail.CoverageResults);
        Assert.Empty(detail.LintResults);
        Assert.Empty(detail.RunMetrics);
        Assert.Empty(detail.Artifacts);

        var results = await _repo.GetRunResultSummariesAsync(detail, ct);

        Assert.NotNull(results.Coverage);
        Assert.Equal((0.8, 100, run.Id), (results.Coverage.LineRate, results.Coverage.LinesValid, results.Coverage.RunId));
        Assert.Empty(results.Coverage.Files);
        Assert.NotNull(results.Lint);
        Assert.Equal(("new", 2, true), (results.Lint.Tool, results.Lint.WarningCount, results.Lint.Passed));
        Assert.Equal(("loc.total", 42d, "build"), (results.Metrics.Single().Key, results.Metrics.Single().Value, results.Metrics.Single().StepName));
        var artifact = Assert.Single(results.Artifacts);
        Assert.Equal(("drop", "develop", "abc123", (string?)null), (artifact.Name, artifact.BranchName, artifact.CommitHash, artifact.Sha256));
    }

    /// <summary>The source endpoint reads four columns of the pipeline.</summary>
    [Fact]
    public async Task R483_GetPipelineSourceFieldsAsync_GivesTheProjectNameAndSourceBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        var p = new Pipeline { Name = "Candidate", YamlDefinition = "y", ProjectId = 4, SourceBranch = "develop", SourceRepositoryId = 9 };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(ct);

        Assert.Equal(new PipelineSourceFields(4, "Candidate", "develop", 9), await _repo.GetPipelineSourceFieldsAsync(p.Id, ct));
        Assert.Null(await _repo.GetPipelineSourceFieldsAsync(p.Id + 1000, ct));
    }

    [Fact]
    public async Task GetRunDetailAsync_Found_IncludesStepRuns()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, StepName = "build", StageName = "build", Order = 1, Status = TaskExecutionStatus.Pending });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRunDetailAsync(run.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Single(result.StepRuns);
    }

    [Fact]
    public async Task GetRunDetailAsync_NotFound_ReturnsNull()
    {
        var result = await _repo.GetRunDetailAsync(999, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    // --- GetPipelineRunWithPipelineAsync ---

    [Fact]
    public async Task GetPipelineRunWithPipelineAsync_Found_IncludesPipeline()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetPipelineRunWithPipelineAsync(run.Id, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(result.Pipeline);
    }

    [Fact]
    public async Task GetRootRunReferencesAsync_ResolvesNestedChildToRootPipeline()
    {
        var rootPipeline = new Pipeline { Name = "root-orchestrator", YamlDefinition = "y" };
        var middlePipeline = new Pipeline { Name = "middle", YamlDefinition = "y" };
        var childPipeline = new Pipeline { Name = "release-child", YamlDefinition = "y" };
        _db.Pipelines.AddRange(rootPipeline, middlePipeline, childPipeline);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var rootRun = new PipelineRun { PipelineId = rootPipeline.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };
        var middleRun = new PipelineRun { PipelineId = middlePipeline.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };
        var childRun = new PipelineRun { PipelineId = childPipeline.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.AddRange(rootRun, middleRun, childRun);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun
            {
                PipelineRunId = rootRun.Id,
                StageName = "orchestrate",
                StepName = "middle",
                TriggeredRunId = middleRun.Id
            },
            new PipelineStepRun
            {
                PipelineRunId = middleRun.Id,
                StageName = "release",
                StepName = "child",
                TriggeredRunId = childRun.Id
            });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRootRunReferencesAsync(
            [childRun.Id, middleRun.Id, rootRun.Id],
            TestContext.Current.CancellationToken);

        Assert.All(result.Values, root => Assert.Equal(rootRun.Id, root.RunId));
        Assert.All(result.Values, root => Assert.Equal(rootPipeline.Id, root.PipelineId));
        Assert.All(result.Values, root => Assert.Equal("root-orchestrator", root.PipelineName));
    }

    // --- AreAllStepsInStageCompletedAsync ---

    [Fact]
    public async Task AreAllStepsInStageCompleted_AllSucceeded_ReturnsTrue()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s1", StageName = "build", Order = 1, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s2", StageName = "build", Order = 2, Status = TaskExecutionStatus.Success }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.AreAllStepsInStageCompletedAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        Assert.True(result);
    }

    [Fact]
    public async Task AreAllStepsInStageCompleted_OnePending_ReturnsFalse()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s1", StageName = "build", Order = 1, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s2", StageName = "build", Order = 2, Status = TaskExecutionStatus.Pending }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.AreAllStepsInStageCompletedAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Fact]
    public async Task AreAllStepsInStageCompleted_MixedSuccessAndFailed_ReturnsTrue()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s1", StageName = "build", Order = 1, Status = TaskExecutionStatus.Success },
            new PipelineStepRun { PipelineRunId = run.Id, StepName = "s2", StageName = "build", Order = 2, Status = TaskExecutionStatus.Failed }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.AreAllStepsInStageCompletedAsync(run.Id, "build", ct: TestContext.Current.CancellationToken);
        Assert.True(result);
    }

    // --- UpdatePipelineRunStatusAsync ---

    [Fact]
    public async Task UpdatePipelineRunStatus_SetsStatusAndCompletedAt()
    {
        var p = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(p);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var run = new PipelineRun { PipelineId = p.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.UpdatePipelineRunStatusAsync(run.Id, PipelineStatus.Success, ct: TestContext.Current.CancellationToken);

        var updated = await _db.PipelineRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.Equal(PipelineStatus.Success, updated!.Status);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task UpdatePipelineRunStatus_RunNotFound_DoesNothing()
    {
        await _repo.UpdatePipelineRunStatusAsync(999, PipelineStatus.Failed, ct: TestContext.Current.CancellationToken);
        // No exception
    }

    // --- SaveChangesAsync ---

    [Fact]
    public async Task SaveChangesAsync_PersistsPendingChanges()
    {
        var p = new Pipeline { Name = "Unsaved", YamlDefinition = "y" };
        _db.Pipelines.Add(p);

        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, await _db.Pipelines.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // --- GetPipelinesPagedProjectedAsync projectId filter (X4F9 / #13) ---

    [Fact]
    public async Task GetPipelinesPagedProjectedAsync_FiltersByProjectId()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "P1-A", YamlDefinition = "y", ProjectId = 1 },
            new Pipeline { Name = "P1-B", YamlDefinition = "y", ProjectId = 1 },
            new Pipeline { Name = "P2-A", YamlDefinition = "y", ProjectId = 2 }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (items, total) = await _repo.GetPipelinesPagedProjectedAsync(
            search: null, triggerType: null, environmentId: null, projectServerId: null, projectId: 1,
            page: 1, pageSize: 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.All(items, p => Assert.Equal(1, p.ProjectId));
        Assert.Equal(new[] { "P1-A", "P1-B" }, items.Select(p => p.Name).ToArray());
    }

    // --- GetCoverageTrendAsync (K) - #6 ---

    [Fact]
    public async Task GetCoverageTrendAsync_DedupesToOnePointPerRun_OrderedOldestToNewest_ExcludesOtherPipelines()
    {
        var runA = new PipelineRun { Id = 1, PipelineId = 1, StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var runB = new PipelineRun { Id = 2, PipelineId = 1, StartedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
        var runOther = new PipelineRun { Id = 3, PipelineId = 2, StartedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc) };
        _db.PipelineRuns.AddRange(runA, runB, runOther);
        _db.CoverageResults.AddRange(
            // run A and run B each have MULTIPLE results - the trend must collapse to one point per run.
            new CoverageResult { PipelineRun = runA, LineRate = 0.50, BranchRate = 0.40, CreatedAt = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc) },
            new CoverageResult { PipelineRun = runA, LineRate = 0.60, BranchRate = 0.45, CreatedAt = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc) },
            new CoverageResult { PipelineRun = runB, LineRate = 0.80, BranchRate = 0.70, CreatedAt = new DateTime(2026, 1, 2, 1, 0, 0, DateTimeKind.Utc) },
            new CoverageResult { PipelineRun = runB, LineRate = 0.82, BranchRate = 0.72, CreatedAt = new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc) },
            // Different pipeline - must be excluded entirely.
            new CoverageResult { PipelineRun = runOther, LineRate = 0.99, BranchRate = 0.99, CreatedAt = new DateTime(2026, 1, 3, 1, 0, 0, DateTimeKind.Utc) }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var trend = await _repo.GetCoverageTrendAsync(runId: 1, take: 10, ct: TestContext.Current.CancellationToken);

        // One point per run, oldest run first (A) then newest (B); the other pipeline's run is excluded.
        Assert.Equal(new[] { 1, 2 }, trend.Select(t => t.RunId).ToArray());
        // Assert the retained value belongs to the CORRECT run (run 1's point must be one of run A's two
        // values, run 2's one of run B's) - catches cross-run contamination the old flat Contains missed.
        // (Which of a run's two rows wins is an ordering the InMemory provider does not model faithfully;
        // the exact most-recent-CreatedAt tie-break is asserted against real Postgres in integration tests.)
        var byRun = trend.ToDictionary(t => t.RunId, t => t.LineRate);
        Assert.Contains(byRun[1], new[] { 0.50, 0.60 });
        Assert.Contains(byRun[2], new[] { 0.80, 0.82 });
    }

    [Fact]
    public async Task GetCoverageTrendAsync_HonoursTake_KeepsMostRecentRuns()
    {
        var runA = new PipelineRun { Id = 1, PipelineId = 1, StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var runB = new PipelineRun { Id = 2, PipelineId = 1, StartedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
        _db.PipelineRuns.AddRange(runA, runB);
        _db.CoverageResults.AddRange(
            new CoverageResult { PipelineRun = runA, LineRate = 0.50, CreatedAt = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc) },
            new CoverageResult { PipelineRun = runB, LineRate = 0.80, CreatedAt = new DateTime(2026, 1, 2, 1, 0, 0, DateTimeKind.Utc) }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var trend = await _repo.GetCoverageTrendAsync(runId: 1, take: 1, ct: TestContext.Current.CancellationToken);

        Assert.Single(trend);
        Assert.Equal(2, trend[0].RunId); // the most recent run is kept
    }

    [Fact]
    public async Task GetCoverageTrendAsync_UnknownRun_ReturnsEmpty()
    {
        var trend = await _repo.GetCoverageTrendAsync(runId: 404, take: 10, ct: TestContext.Current.CancellationToken);
        Assert.Empty(trend);
    }

    // --- ReserveNextBuildNumberAsync (BUILD_PIPELINE_RUNNUMBER) ---

    [Fact]
    public async Task ReserveNextBuildNumberAsync_StartsAtOneAndIncrementsByOne()
    {
        _db.Pipelines.Add(new Pipeline { Name = "Alpha", YamlDefinition = "y" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var pipelineId = _db.Pipelines.Single().Id;

        var first = await _repo.ReserveNextBuildNumberAsync(pipelineId, TestContext.Current.CancellationToken);
        var second = await _repo.ReserveNextBuildNumberAsync(pipelineId, TestContext.Current.CancellationToken);
        var third = await _repo.ReserveNextBuildNumberAsync(pipelineId, TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
    }

    [Fact]
    public async Task ReserveNextBuildNumberAsync_CountsPerPipelineNotGlobally()
    {
        _db.Pipelines.AddRange(
            new Pipeline { Name = "Alpha", YamlDefinition = "y" },
            new Pipeline { Name = "Beta", YamlDefinition = "y" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var alphaId = _db.Pipelines.Single(p => p.Name == "Alpha").Id;
        var betaId = _db.Pipelines.Single(p => p.Name == "Beta").Id;

        await _repo.ReserveNextBuildNumberAsync(alphaId, TestContext.Current.CancellationToken);
        await _repo.ReserveNextBuildNumberAsync(alphaId, TestContext.Current.CancellationToken);
        var betaFirst = await _repo.ReserveNextBuildNumberAsync(betaId, TestContext.Current.CancellationToken);

        // Beta has its own sequence: a busy sibling pipeline must not advance it.
        Assert.Equal(1, betaFirst);
    }

    [Fact]
    public async Task ReserveNextBuildNumberAsync_SurvivesRunRetention()
    {
        _db.Pipelines.Add(new Pipeline { Name = "Alpha", YamlDefinition = "y" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var pipelineId = _db.Pipelines.Single().Id;
        await _repo.ReserveNextBuildNumberAsync(pipelineId, TestContext.Current.CancellationToken);
        await _repo.ReserveNextBuildNumberAsync(pipelineId, TestContext.Current.CancellationToken);

        // Purging every run (retention) must not rewind the counter: a derived application version
        // would otherwise be reissued for different source.
        _db.PipelineRuns.RemoveRange(_db.PipelineRuns);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var next = await _repo.ReserveNextBuildNumberAsync(pipelineId, TestContext.Current.CancellationToken);

        Assert.Equal(3, next);
    }

    [Fact]
    public async Task ReserveNextBuildNumberAsync_UnknownPipeline_ReturnsZero()
    {
        var reserved = await _repo.ReserveNextBuildNumberAsync(404, TestContext.Current.CancellationToken);
        Assert.Equal(0, reserved);
    }

    public void Dispose() => _db.Dispose();
}
