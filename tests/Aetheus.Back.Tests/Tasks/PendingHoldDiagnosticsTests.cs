// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Tasks;

/// <summary>
/// Recette R-5: four pipeline tasks stayed Pending for 71 minutes with the line "its agent is not claiming work
/// right now" and nothing that told offline, lapsed polling, an update fence or a busy agent apart. The hold
/// diagnosis names which of them applies, from what the backend has.
/// </summary>
public sealed class PendingHoldDiagnosticsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 21, 13, 52, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private const string CapabilitiesJson = "[\"shell.execute\",\"pipeline.build\"]";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    public void Dispose() => _db.Dispose();

    private static ServerTask Task(OperationKind operation = OperationKind.None, TimeSpan? age = null) => new()
    {
        Id = 11323,
        ServerId = 5,
        Name = "build",
        PipelineRunId = 1352,
        Operation = operation,
        Status = TaskExecutionStatus.Pending,
        CreatedAt = Now - (age ?? TimeSpan.FromMinutes(71))
    };

    private static PendingHoldServerFacts Server(
        ServerStatus status = ServerStatus.Online,
        DateTime? lease = null,
        TimeSpan? heartbeatAge = null,
        bool reserved = false,
        DateTime? reservedAt = null,
        int? protocol = 2,
        string? capabilities = CapabilitiesJson,
        DateTime? deletedAt = null,
        IReadOnlyList<PendingHoldActiveTask>? active = null,
        int pending = 4) => new(
            5, "runner-a", status, deletedAt, Now - (heartbeatAge ?? TimeSpan.FromSeconds(10)),
            lease ?? Now - TimeSpan.FromMinutes(70), "0123456789abcdef0123456789abcdef", "1.4.0", protocol, capabilities,
            reserved, reservedAt, active ?? [], pending);

    private static PendingHoldDiagnosis Diagnose(ServerTask task, PendingHoldServerFacts? server)
        => PendingHoldDiagnostics.Diagnose(task, server, Now, Grace, HeartbeatTimeout, LeaseDuration);

    [Fact]
    public void OnlineServerWhosePollingLeaseLapsedWhileItsHeartbeatIsFresh_NamesALapsedLeaseAndWhenItLastClaimed()
    {
        var diagnosis = Diagnose(Task(), Server(lease: Now - TimeSpan.FromMinutes(70)));

        Assert.Equal(PendingHoldCause.PollingLeaseLapsed, diagnosis.Cause);
        Assert.Contains("heartbeat=10s ago (fresh)", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("lapsed 1h10m ago (last claim request about 1h12m ago)", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("blockers=none", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskAgeAgainstTheGrace_IsReported()
    {
        var diagnosis = Diagnose(Task(age: TimeSpan.FromMinutes(71)), Server());

        Assert.Contains("taskAge=1h11m", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("grace=24h00m", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("graceLeft=22h49m", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineServerWithAStaleHeartbeat_IsOfflineNotALapsedLease()
    {
        var diagnosis = Diagnose(Task(), Server(ServerStatus.Offline, heartbeatAge: TimeSpan.FromMinutes(40)));

        Assert.Equal(PendingHoldCause.ServerOffline, diagnosis.Cause);
        Assert.Contains("heartbeat=40m00s ago (stale)", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledServer_IsReportedAsDisabled()
        => Assert.Equal(PendingHoldCause.ServerDisabled, Diagnose(Task(), Server(ServerStatus.Disabled)).Cause);

    [Fact]
    public void RetiredServer_WinsOverItsStatus()
        => Assert.Equal(
            PendingHoldCause.ServerRetired,
            Diagnose(Task(), Server(ServerStatus.Offline, deletedAt: Now - TimeSpan.FromDays(1))).Cause);

    [Fact]
    public void OnlineServerThatNeverRenewedItsLease_IsReportedAsNeverStarted()
    {
        var facts = Server() with { LeaseExpiresAt = null };

        var diagnosis = Diagnose(Task(), facts);

        Assert.Equal(PendingHoldCause.PollingNeverStarted, diagnosis.Cause);
        Assert.Contains("pollingLease=never acquired", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void LeaseLiveAgainAtDiagnosisTime_IsReportedAsResumedAndStillListsTheBlockers()
    {
        var diagnosis = Diagnose(Task(), Server(lease: Now + TimeSpan.FromSeconds(90), protocol: null));

        Assert.Equal(PendingHoldCause.PollingResumed, diagnosis.Cause);
        Assert.Contains("pollingLease=live for another 1m30s", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("agent protocol unsupported or unreported", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateReservationCreatedBeforeTheTask_FencesIt_ButNotATaskOlderThanTheReservation()
    {
        var reservedAt = Now - TimeSpan.FromMinutes(80);

        var fenced = Diagnose(Task(age: TimeSpan.FromMinutes(71)), Server(reserved: true, reservedAt: reservedAt));
        var older = Diagnose(Task(age: TimeSpan.FromMinutes(90)), Server(reserved: true, reservedAt: reservedAt));

        Assert.Contains("agent-update-reserved since", fenced.Summary, StringComparison.Ordinal);
        Assert.Contains("blockers=none", older.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationTheAgentDoesNotDeclare_IsNamedAsACapabilityGap()
    {
        var diagnosis = Diagnose(
            Task(OperationKind.PipelineCollectArtifacts),
            Server(capabilities: CapabilitiesJson));

        Assert.Contains("capability 'artifact.collect' not declared by the agent", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfUpdateInFlight_IsABlockerForOtherWork_ButNotForTheSelfUpdateItself()
    {
        var active = new PendingHoldActiveTask[]
        {
            new(900, TaskExecutionStatus.Assigned, OperationKind.AgentSelfUpdate, Now - TimeSpan.FromMinutes(65))
        };

        var other = Diagnose(Task(), Server(active: active));
        var selfUpdate = Diagnose(Task(OperationKind.AgentSelfUpdate), Server(active: active));

        Assert.Contains("agent self-update #900 Assigned", other.Summary, StringComparison.Ordinal);
        Assert.Contains("blockers=none", selfUpdate.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyServer_ListsItsActiveTasksWithTheirDuration_AndCapsTheList()
    {
        var active = Enumerable.Range(1, 7)
            .Select(index => new PendingHoldActiveTask(
                index, TaskExecutionStatus.Running, OperationKind.None, Now - TimeSpan.FromMinutes(index)))
            .ToList();

        var diagnosis = Diagnose(Task(), Server(active: active));

        Assert.Contains("serverActive=7 [#1 Running None for 1m00s", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("+2 more", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("serverPending=4", diagnosis.Summary, StringComparison.Ordinal);
        Assert.Contains("MaxConcurrentTasks is not reported", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingServerRow_IsReportedAsUnknownRatherThanThrowing()
    {
        var diagnosis = Diagnose(Task(), null);

        Assert.Equal(PendingHoldCause.ServerUnknown, diagnosis.Cause);
        Assert.Contains("pinnedServer=#5", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_ReturnsTheServerLeaseUpdateFenceAndItsActiveAndPendingTasks()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.Servers.Add(new Server
        {
            Id = 5,
            Name = "runner-a",
            Hostname = "runner-a",
            Status = ServerStatus.Online,
            AgentSessionId = "session-a",
            AgentSessionLeaseExpiresAt = Now - TimeSpan.FromMinutes(70),
            AgentUpdateReserved = true,
            AgentUpdateReservedAt = Now - TimeSpan.FromMinutes(80),
            AgentProtocolVersion = 2,
            AgentCapabilitiesJson = CapabilitiesJson,
            LastHeartbeat = Now
        });
        _db.Tasks.AddRange(
            NewTask(1, TaskExecutionStatus.Running, startedAt: Now - TimeSpan.FromMinutes(55)),
            NewTask(2, TaskExecutionStatus.Assigned, startedAt: null),
            NewTask(3, TaskExecutionStatus.Pending, startedAt: null),
            NewTask(4, TaskExecutionStatus.Pending, startedAt: null),
            NewTask(5, TaskExecutionStatus.Success, startedAt: null));
        await _db.SaveChangesAsync(ct);

        var facts = await new TaskRepository(_db, TimeProvider.System).GetPendingHoldFactsAsync(5, ct);

        Assert.NotNull(facts);
        Assert.Equal("runner-a", facts.Name);
        Assert.True(facts.UpdateReserved);
        Assert.Equal(Now - TimeSpan.FromMinutes(70), facts.LeaseExpiresAt);
        Assert.Equal([1, 2], facts.ActiveTasks.Select(task => task.Id).Order());
        Assert.Equal(2, facts.PendingTaskCount);
        Assert.Null(await new TaskRepository(_db, TimeProvider.System).GetPendingHoldFactsAsync(404, ct));

        static ServerTask NewTask(int id, TaskExecutionStatus status, DateTime? startedAt) => new()
        {
            Id = id,
            ServerId = 5,
            Name = $"task-{id}",
            Command = "echo",
            Status = status,
            StartedAt = startedAt,
            CreatedAt = Now - TimeSpan.FromHours(2)
        };
    }
}
