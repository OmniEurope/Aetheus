// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-39: a deployment whose confirmation was missed waits for a resume instead of failing. These
/// tests run the decision, the resume, the cancellation and the expiry against the real repository (EF
/// in-memory), with only the scheduler substituted where the dispatch itself is not the point.
/// </summary>
public sealed class PipelineResumeWindowTests : IDisposable
{
    private const int RunId = 4100;
    private const int PipelineId = 41;

    /// <summary>The shape of release-fast and deploy-prod: build, start the new colour, switch, confirm
    /// within ten minutes, record the release, and the failed() rollback.</summary>
    private const string DeployYaml = """
        name: deploy
        trigger: manual
        stages:
          - name: Build
            agent: linux-01
            steps:
              - name: compile
                shell: build.sh
          - name: Up
            agent: linux-01
            deployment_role: start
            depends_on: [Build]
            steps:
              - name: start
                shell: up.sh
          - name: Switch
            agent: linux-01
            depends_on: [Up]
            steps:
              - name: point-traffic
                shell: switch.sh
          - name: Confirm
            agent: linux-01
            approval_timeout_minutes: 10
            depends_on: [Switch]
            steps:
              - name: confirmed
                shell: echo confirmed
          - name: Record release
            agent: linux-01
            depends_on: [Confirm]
            steps:
              - name: record
                shell: record.sh
          - name: Rollback
            agent: linux-01
            condition: "failed()"
            deployment_role: rollback
            depends_on: [Record release]
            steps:
              - name: restore
                shell: rollback.sh
        """;

    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
    private readonly IHubContext<PipelineHub> _hub = Substitute.For<IHubContext<PipelineHub>>();
    private readonly IClientProxy _clients = Substitute.For<IClientProxy>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IPipelineRunScheduler _scheduler = Substitute.For<IPipelineRunScheduler>();
    private readonly IPipelineChildRunLauncher _launcher = Substitute.For<IPipelineChildRunLauncher>();

    public PipelineResumeWindowTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
        var hubClients = Substitute.For<IHubClients>();
        hubClients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(_clients);
        hubClients.Group(Arg.Any<string>()).Returns(_clients);
        _hub.Clients.Returns(hubClients);
    }

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- The decision: wait, or end as today ---

    [Fact]
    public async Task AMissedConfirmationWhoseRollbackSucceeded_WaitsADayForAResume_InsteadOfFailing()
    {
        SeedMissedConfirmation();

        var parked = await ParkAsync();

        Assert.True(parked);
        var run = await ReadRunAsync();
        Assert.Equal(PipelineStatus.AwaitingResume, run.Status);
        Assert.Equal(Now.AddHours(24), run.ResumeDeadline);
        Assert.Null(run.CompletedAt);
        await _clients.Received(1).SendCoreAsync(
            PipelineResumeWindow.AwaitingResumeMessage,
            Arg.Is<object?[]>(args => (int)args[0]! == RunId && (DateTime)args[1]! == Now.AddHours(24)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheStageResumeWindow_ReadFromTheDefinition_SetsTheDeadline()
    {
        SeedMissedConfirmation(yaml: DeployYaml.Replace(
            "approval_timeout_minutes: 10", "approval_timeout_minutes: 10\n    resume_window_hours: 2", StringComparison.Ordinal));

        Assert.True(await ParkAsync());

        Assert.Equal(Now.AddHours(2), (await ReadRunAsync()).ResumeDeadline);
    }

    [Fact]
    public async Task AResumeWindowOfZero_KeepsTodaysEnd()
    {
        SeedMissedConfirmation(yaml: DeployYaml.Replace(
            "approval_timeout_minutes: 10", "approval_timeout_minutes: 10\n    resume_window_hours: 0", StringComparison.Ordinal));

        Assert.False(await ParkAsync());

        Assert.Equal(PipelineStatus.Running, (await ReadRunAsync()).Status);
    }

    /// <summary>A refusal is a human decision: the run ends as it does today.</summary>
    [Fact]
    public async Task ARefusedConfirmation_DoesNotWait()
    {
        SeedMissedConfirmation(decision: ApprovalStatus.Rejected);

        Assert.False(await ParkAsync());

        Assert.Equal(PipelineStatus.Running, (await ReadRunAsync()).Status);
    }

    /// <summary>A rollback that failed is never hidden behind a wait: the run fails loudly, as today.</summary>
    [Fact]
    public async Task AFailedRollback_DoesNotWait()
    {
        SeedMissedConfirmation(rollbackStatus: TaskExecutionStatus.Failed);

        Assert.False(await ParkAsync());

        Assert.Equal(PipelineStatus.Running, (await ReadRunAsync()).Status);
    }

    [Fact]
    public async Task AFailureElsewhereThanTheConfirmation_DoesNotWait()
    {
        SeedMissedConfirmation();
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = RunId,
            StageName = "Smoke",
            StepName = "probe",
            Status = TaskExecutionStatus.Failed
        });
        await _db.SaveChangesAsync(Ct);

        Assert.False(await ParkAsync());
    }

    [Fact]
    public async Task ACancelledRun_DoesNotWait()
    {
        SeedMissedConfirmation();
        await _repo.RequestPipelineRunCancellationAsync(RunId, PipelineRunCancellation.ByUser("alice"), Ct);

        Assert.False(await ParkAsync());
    }

    /// <summary>The expiry sweep re-opens the run with its deadline in the past so it can end: it must
    /// not be parked a second time.</summary>
    [Fact]
    public async Task ARunWhoseWindowClosed_IsNotParkedAgain()
    {
        SeedMissedConfirmation(resumeDeadline: Now.AddMinutes(-1));

        Assert.False(await ParkAsync());

        Assert.Equal(PipelineStatus.Running, (await ReadRunAsync()).Status);
    }

    [Fact]
    public async Task AConfirmationGivenAfterAResume_DoesNotWait()
    {
        SeedMissedConfirmation();
        // The replay went through: the second window was confirmed, nothing failed.
        foreach (var step in _db.PipelineStepRuns.Where(step => step.PipelineRunId == RunId && step.StageName != PipelineRunHelpers.SystemCleanupStage))
            step.Status = step.StageName == "Rollback" ? TaskExecutionStatus.Cancelled : TaskExecutionStatus.Success;
        _db.PipelineApprovals.Add(ConfirmApproval(ApprovalStatus.Approved, resolvedAt: Now.AddMinutes(-1)));
        await _db.SaveChangesAsync(Ct);

        Assert.False(await ParkAsync());
    }

    // --- Resume ---

    [Fact]
    public void AResume_ReplaysFromTheStartOfTheDeployment_NotTheBuild()
    {
        var definition = YamlParsingHelper.ParseAndValidate(DeployYaml)!;

        Assert.Equal(["Up", "Switch", "Confirm", "Record release", "Rollback"], PipelineResumeWindow.StagesToReplay(definition));
    }

    [Fact]
    public async Task Resume_ResetsTheDeploymentStages_DispatchesThem_AndAuditsWhoAskedForIt()
    {
        SeedMissedConfirmation();
        Assert.True(await ParkAsync());

        await Control().ResumeAwaitingRunAsync(RunId, "alice", _scheduler, _launcher, Ct);

        var run = await ReadRunAsync();
        Assert.Equal(PipelineStatus.Running, run.Status);
        // One window per run: the resume keeps the deadline of the first missed confirmation.
        Assert.Equal(Now.AddHours(24), run.ResumeDeadline);
        var steps = await _db.PipelineStepRuns.AsNoTracking().Where(step => step.PipelineRunId == RunId).ToListAsync(Ct);
        Assert.All(steps.Where(step => step.StageName is "Up" or "Switch" or "Confirm" or "Record release" or "Rollback"), step =>
        {
            Assert.Equal(TaskExecutionStatus.Pending, step.Status);
            Assert.Null(step.CompletedAt);
            Assert.Null(step.FailureReason);
            Assert.Null(step.OutputVariablesJson);
            Assert.Null(step.TaskId);
        });
        // Same build: the image is not rebuilt, and its outputs stay for the replay to read.
        var build = Assert.Single(steps, step => step.StageName == "Build");
        Assert.Equal(TaskExecutionStatus.Success, build.Status);
        Assert.Equal("""{"IMAGE":"aetheus:1.2.3"}""", build.OutputVariablesJson);
        Assert.Equal(TaskExecutionStatus.Pending, Assert.Single(steps, step => step.StageName == PipelineRunHelpers.SystemCleanupStage).Status);
        await _scheduler.Received(1).DispatchNextStageSerializedAsync(
            Arg.Is<PipelineRun>(r => r.Id == RunId && r.Status == PipelineStatus.Running),
            Arg.Any<PipelineYamlDefinition>(), _launcher, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "Resumed", "PipelineRun", RunId,
            Arg.Is<string>(details => details.Contains("by alice", StringComparison.Ordinal)
                && details.Contains("replays Up, Switch, Confirm, Record release, Rollback", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The second confirmation missed too, inside the window: the rollback ran again and the run
    /// waits again, but only until the deadline it was first given (one window per run).</summary>
    [Fact]
    public async Task ASecondMissedConfirmation_WaitsAgain_UntilTheFirstDeadline()
    {
        SeedMissedConfirmation();
        Assert.True(await ParkAsync());
        var firstDeadline = (await ReadRunAsync()).ResumeDeadline;
        await Control().ResumeAwaitingRunAsync(RunId, "alice", _scheduler, _launcher, Ct);
        _clock.Advance(TimeSpan.FromHours(3));
        await SettleSecondMissedConfirmationAsync();

        Assert.True(await ParkAsync());

        var run = await ReadRunAsync();
        Assert.Equal(PipelineStatus.AwaitingResume, run.Status);
        Assert.Equal(firstDeadline, run.ResumeDeadline);
        Assert.NotEqual(Now.AddHours(24), run.ResumeDeadline);
        // Announced twice (each park), both times with the first deadline.
        await _clients.Received(2).SendCoreAsync(
            PipelineResumeWindow.AwaitingResumeMessage,
            Arg.Is<object?[]>(args => (DateTime)args[1]! == firstDeadline),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The window of the first miss has elapsed by the time the replay misses its confirmation
    /// too: no new wait, the run ends rolled back exactly as at a deadline expiry.</summary>
    [Fact]
    public async Task ASecondMissedConfirmation_AfterTheFirstDeadline_DoesNotWaitAgain()
    {
        SeedMissedConfirmation();
        Assert.True(await ParkAsync());
        _clock.Advance(TimeSpan.FromHours(23));
        await Control().ResumeAwaitingRunAsync(RunId, "alice", _scheduler, _launcher, Ct);
        _clock.Advance(TimeSpan.FromHours(2));
        await SettleSecondMissedConfirmationAsync();

        Assert.False(await ParkAsync());

        var run = await ReadRunAsync();
        Assert.Equal(PipelineStatus.Running, run.Status);
        Assert.Empty(await _repo.GetExpiredResumeWaitRunIdsAsync(Now, Ct));
    }

    // The replay ran: up and switch succeeded, the new confirmation timed out, the rollback restored.
    private async Task SettleSecondMissedConfirmationAsync()
    {
        foreach (var step in _db.PipelineStepRuns.Where(step => step.PipelineRunId == RunId))
        {
            step.Status = step.StageName switch
            {
                "Up" or "Switch" or "Rollback" => TaskExecutionStatus.Success,
                "Confirm" => TaskExecutionStatus.Failed,
                "Record release" => TaskExecutionStatus.Cancelled,
                _ => step.Status
            };
        }
        _db.PipelineApprovals.Add(ConfirmApproval(ApprovalStatus.TimedOut, resolvedAt: Now));
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AResumeAfterTheDeadline_IsRefused_Audited_AndReplaysNothing()
    {
        SeedMissedConfirmation();
        Assert.True(await ParkAsync());
        _clock.Advance(TimeSpan.FromHours(25));

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => Control().ResumeAwaitingRunAsync(RunId, "alice", _scheduler, _launcher, Ct));

        Assert.Contains("elapsed", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(PipelineStatus.AwaitingResume, (await ReadRunAsync()).Status);
        Assert.Equal(TaskExecutionStatus.Success,
            (await _db.PipelineStepRuns.AsNoTracking().SingleAsync(step => step.PipelineRunId == RunId && step.StageName == "Up", Ct)).Status);
        await _scheduler.DidNotReceive().DispatchNextStageSerializedAsync(
            Arg.Any<PipelineRun>(), Arg.Any<PipelineYamlDefinition>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "Resumed", "PipelineRun", RunId,
            Arg.Is<string>(details => details.Contains("refused", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AResumeOfARunThatIsNotWaiting_IsAConflict()
    {
        SeedMissedConfirmation();

        await Assert.ThrowsAsync<ConflictException>(
            () => Control().ResumeAwaitingRunAsync(RunId, "alice", _scheduler, _launcher, Ct));

        Assert.Equal(PipelineStatus.Running, (await ReadRunAsync()).Status);
        await _scheduler.DidNotReceive().DispatchNextStageSerializedAsync(
            Arg.Any<PipelineRun>(), Arg.Any<PipelineYamlDefinition>(), Arg.Any<IPipelineChildRunLauncher>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AResumeOfAnUnknownRun_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(
            () => Control().ResumeAwaitingRunAsync(999, "alice", _scheduler, _launcher, Ct));
    }

    [Fact]
    public async Task ReopenOnlyMovesARunThatStillWaits()
    {
        SeedMissedConfirmation();

        Assert.Equal(ResumeReopenOutcome.NotWaiting, await _repo.ReopenAwaitingRunAsync(RunId, ["Up"], Now, Ct));
        Assert.True(await ParkAsync());
        Assert.Equal(ResumeReopenOutcome.Reopened, await _repo.ReopenAwaitingRunAsync(RunId, ["Up"], Now, Ct));
        Assert.Equal(ResumeReopenOutcome.NotWaiting, await _repo.ReopenAwaitingRunAsync(RunId, ["Up"], Now, Ct));
    }

    // --- Cancel ---

    [Fact]
    public async Task CancellingAWaitingRun_ClosesItThroughTheCancellationPath_AndItIsNotParkedAgain()
    {
        SeedMissedConfirmation();
        Assert.True(await ParkAsync());

        var cancelled = await Control().CancelRunAsync(
            RunId, PipelineRunCancellation.ByUser("alice"), _scheduler, _launcher, Ct);

        Assert.True(cancelled);
        var run = await ReadRunAsync();
        Assert.Equal(PipelineStatus.Running, run.Status);
        Assert.True(PipelineRunHelpers.HasCancellationRequest(run.AdditionalVariablesJson));
        await _audit.Received(1).LogAsync("Cancelled", "PipelineRun", RunId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        // The scheduler pass that follows finalizes the run Cancelled; it must not park it again.
        await _scheduler.Received(1).AdvanceToNextStageOrCompleteAsync(RunId, _launcher, Arg.Any<CancellationToken>());
        Assert.False(await ParkAsync());
    }

    // --- Expiry ---

    [Fact]
    public async Task AWindowThatElapses_ClosesTheRun_WithANotice_AndAnAuditEntry()
    {
        SeedMissedConfirmation();
        Assert.True(await ParkAsync());
        Assert.Empty(await _repo.GetExpiredResumeWaitRunIdsAsync(Now, Ct));
        Assert.False(await Control().CloseExpiredResumeWaitAsync(RunId, _scheduler, _launcher, Ct));
        _clock.Advance(TimeSpan.FromHours(24));

        Assert.Equal([RunId], await _repo.GetExpiredResumeWaitRunIdsAsync(Now, Ct));
        var closed = await Control().CloseExpiredResumeWaitAsync(RunId, _scheduler, _launcher, Ct);

        Assert.True(closed);
        var run = await ReadRunAsync();
        Assert.Equal(PipelineStatus.Running, run.Status);
        Assert.Contains(PipelineRunNotices.ResumeWindowExpired, run.WarningsJson, StringComparison.Ordinal);
        await _audit.Received(1).LogAsync(
            "ResumeWindowExpired", "PipelineRun", RunId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _scheduler.Received(1).ReconcileRunAsync(RunId, _launcher, Arg.Any<CancellationToken>());
        // The pass that follows runs the cleanup and ends the run RolledBack: it does not wait again.
        Assert.False(await ParkAsync());
        Assert.Empty(await _repo.GetExpiredResumeWaitRunIdsAsync(Now, Ct));
    }

    // --- What the run page reads ---

    [Theory]
    [InlineData(PipelineStatus.AwaitingResume, true)]
    [InlineData(PipelineStatus.Running, false)]
    [InlineData(PipelineStatus.Cancelled, false)]
    public void TheRunDto_CarriesTheDeadline_OnlyWhileTheRunWaits(PipelineStatus status, bool shown)
    {
        var deadline = Now.AddHours(24);

        var dto = PipelineRunDtoMapper.MapRunToDto(new PipelineRun
        {
            Id = RunId,
            PipelineId = PipelineId,
            Status = status,
            ResumeDeadline = deadline,
            Pipeline = new Pipeline { Id = PipelineId, Name = "release", YamlDefinition = DeployYaml }
        });

        Assert.Equal(shown ? deadline : null, dto.ResumeDeadline);
    }

    [Fact]
    public void AWaitingRun_IsNotTerminal()
    {
        Assert.False(PipelineStatus.AwaitingResume.IsTerminal());
    }

    // --- Helpers ---

    private PipelineRunControlService Control() => new(
        _repo, _hub, new PipelineRunDefinitionParser(NullLogger<PipelineRunDefinitionParser>.Instance), _audit, _clock);

    private async Task<bool> ParkAsync()
    {
        var run = await _repo.GetPipelineRunWithPipelineAsync(RunId, Ct);
        Assert.NotNull(run);
        var definition = YamlParsingHelper.ParseAndValidate(run.YamlSnapshot ?? run.Pipeline!.YamlDefinition)!;
        var pending = await _repo.GetPendingStepRunsAsync(RunId, Ct);
        var window = new PipelineResumeWindow(_repo, _hub, _clock, NullLogger.Instance);
        return await window.TryParkAsync(run, definition, pending, Ct);
    }

    private async Task<PipelineRun> ReadRunAsync() =>
        await _db.PipelineRuns.AsNoTracking().SingleAsync(run => run.Id == RunId, Ct);

    private PipelineApproval ConfirmApproval(ApprovalStatus status, DateTime resolvedAt) => new()
    {
        PipelineRunId = RunId,
        Scope = ApprovalScope.Pipeline,
        StageName = "Confirm",
        TimeoutMinutes = 10,
        Status = status,
        RequestedAt = resolvedAt.AddMinutes(-10),
        ResolvedAt = resolvedAt
    };

    /// <summary>The run as the engine leaves it after the confirmation timed out and the rollback ran:
    /// Running, every user stage settled, only the workspace cleanup left.</summary>
    private void SeedMissedConfirmation(
        ApprovalStatus decision = ApprovalStatus.TimedOut,
        TaskExecutionStatus rollbackStatus = TaskExecutionStatus.Success,
        DateTime? resumeDeadline = null,
        string yaml = DeployYaml)
    {
        _db.Pipelines.Add(new Pipeline { Id = PipelineId, Name = "aetheus-release-fast", YamlDefinition = yaml });
        _db.PipelineRuns.Add(new PipelineRun
        {
            Id = RunId,
            PipelineId = PipelineId,
            Status = PipelineStatus.Running,
            YamlSnapshot = yaml,
            ResolvedVariablesJson = "{}",
            StartedAt = Now.AddMinutes(-30),
            ResumeDeadline = resumeDeadline
        });
        Step("Build", "compile", TaskExecutionStatus.Success, outputs: """{"IMAGE":"aetheus:1.2.3"}""");
        Step("Up", "start", TaskExecutionStatus.Success);
        Step("Switch", "point-traffic", TaskExecutionStatus.Success);
        Step("Confirm", "confirmed", TaskExecutionStatus.Failed, failure: "The confirmation window elapsed.");
        Step("Record release", "record", TaskExecutionStatus.Cancelled);
        Step("Rollback", "restore", rollbackStatus);
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = RunId,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        });
        _db.PipelineApprovals.Add(ConfirmApproval(decision, resolvedAt: Now.AddMinutes(-5)));
        _db.SaveChanges();
    }

    private void Step(string stage, string name, TaskExecutionStatus status, string? outputs = null, string? failure = null) =>
        _db.PipelineStepRuns.Add(new PipelineStepRun
        {
            PipelineRunId = RunId,
            StageName = stage,
            StepName = name,
            Status = status,
            OutputVariablesJson = outputs,
            FailureReason = failure,
            StartedAt = Now.AddMinutes(-20),
            CompletedAt = status == TaskExecutionStatus.Pending ? null : Now.AddMinutes(-6),
            TaskId = status == TaskExecutionStatus.Cancelled ? null : 700 + stage.Length
        });
}
