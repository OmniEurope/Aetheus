// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The approval port (AUD-07, suite) answers what the approval service and the reconcile sweeps
/// read and write, on real rows, and the IPipelineRepository facade, which delegates to it, answers the
/// same.</summary>
public sealed class PipelineApprovalRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IPipelineRepository Facade() => new PipelineRepository(
        _db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
        new PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

    private async Task SeedAsync()
    {
        _db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 1, Name = "prod", ApprovalTimeoutMinutes = 30 });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "deploy", ProjectId = 7 },
            new Pipeline { Id = 2, Name = "other" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = 10, PipelineId = 1, Status = PipelineStatus.WaitingForApproval },
            new PipelineRun { Id = 11, PipelineId = 2, Status = PipelineStatus.WaitingForApproval },
            new PipelineRun { Id = 12, PipelineId = 1, Status = PipelineStatus.Cancelled },
            new PipelineRun { Id = 13, PipelineId = 1, Status = PipelineStatus.WaitingForApproval });
        _db.PipelineApprovals.AddRange(
            // Run 10: one old environment approval past its 30 minutes, then a fresh pipeline approval.
            new PipelineApproval { Id = 100, PipelineRunId = 10, StageName = "deploy", EnvironmentId = 1, RequestedAt = Now.AddMinutes(-60) },
            new PipelineApproval
            {
                Id = 101,
                PipelineRunId = 10,
                StageName = "verify",
                Scope = ApprovalScope.Pipeline,
                TimeoutMinutes = 120,
                RequestedAt = Now.AddMinutes(-5)
            },
            new PipelineApproval { Id = 110, PipelineRunId = 11, StageName = "deploy", EnvironmentId = 1, RequestedAt = Now.AddMinutes(-10) },
            // Run 12 ended with a decision still pending; run 13 waits although its only approval is decided.
            new PipelineApproval { Id = 120, PipelineRunId = 12, StageName = "deploy", EnvironmentId = 1, RequestedAt = Now.AddMinutes(-20) },
            new PipelineApproval
            {
                Id = 130,
                PipelineRunId = 13,
                StageName = "deploy",
                EnvironmentId = 1,
                Status = ApprovalStatus.Approved,
                RequestedAt = Now.AddMinutes(-40),
                ResolvedAt = Now.AddMinutes(-30)
            });
        await _db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheReadQueries_ReturnTheRunApprovalsThePendingListTheExpiredAndTheStrandedRuns()
    {
        await SeedAsync();
        IPipelineApprovalRepository port = new PipelineApprovalRepository(_db);

        Assert.Equal([100, 101], (await port.GetApprovalsAsync(10, Ct)).Select(a => a.Id));
        Assert.Equal("prod", (await port.FindApprovalAsync(100, Ct))?.Environment?.Name);
        Assert.Null(await port.FindApprovalAsync(404, Ct));
        Assert.Equal(2, await port.GetPipelineIdForApprovalAsync(110, Ct));
        Assert.Null(await port.GetPipelineIdForApprovalAsync(404, Ct));

        // The pending list keeps waiting runs only, oldest first, within the readable pipelines.
        Assert.Equal([100, 110, 101], (await port.GetPendingApprovalsAsync(null, Ct)).Select(a => a.ApprovalId));
        var readable = await port.GetPendingApprovalsAsync([1], Ct);
        Assert.Equal([100, 101], readable.Select(a => a.ApprovalId));
        Assert.Equal("deploy", readable[0].PipelineName);
        Assert.Equal(7, readable[0].ProjectId);

        // The environment delay (30 min) expired the hour-old request, the pipeline's own (120 min) did not.
        Assert.Equal([100], await port.GetExpiredPendingApprovalIdsAsync(Now, Ct));

        var stranded = Assert.Single(await port.GetStrandedApprovalRunsAsync(Ct));
        Assert.Equal(new StrandedApprovalRun(13, ApprovalStatus.Approved), stranded);
    }

    [Fact]
    public async Task TheWriteQueries_AddDecideAndCloseOnlyWhatIsStillPending()
    {
        await SeedAsync();
        IPipelineApprovalRepository port = new PipelineApprovalRepository(_db);

        await port.AddApprovalAsync(
            new PipelineApproval { Id = 102, PipelineRunId = 10, StageName = "release", EnvironmentId = 1, RequestedAt = Now }, Ct);
        Assert.Equal([100, 101, 102], (await port.GetApprovalsAsync(10, Ct)).Select(a => a.Id));

        var decided = await port.TryResolveApprovalAsync(100, ApprovalStatus.Approved, Now, null, "ok", Ct);
        Assert.NotNull(decided);
        Assert.Equal(ApprovalStatus.Approved, decided.Status);
        Assert.Equal("ok", decided.Comments);
        // A second decision on the same approval finds nothing pending left.
        Assert.Null(await port.TryResolveApprovalAsync(100, ApprovalStatus.Rejected, Now, null, "late", Ct));

        Assert.Equal(1, await port.CloseApprovalsOfEndedRunsAsync(null, Now, EndedRunApprovals.Reason, Ct));
        var closed = await _db.PipelineApprovals.AsNoTracking().SingleAsync(a => a.Id == 120, Ct);
        Assert.Equal(ApprovalStatus.Rejected, closed.Status);
        Assert.Equal(EndedRunApprovals.Reason, closed.Comments);
        Assert.Equal(0, await port.CloseApprovalsOfEndedRunsAsync(12, Now, EndedRunApprovals.Reason, Ct));
    }

    [Fact]
    public async Task TheFacade_AnswersExactlyWhatThePortAnswers()
    {
        await SeedAsync();
        IPipelineApprovalRepository port = new PipelineApprovalRepository(_db);
        IPipelineApprovalRepository facade = Facade();

        Assert.Equal(
            (await port.GetApprovalsAsync(10, Ct)).Select(a => a.Id),
            (await facade.GetApprovalsAsync(10, Ct)).Select(a => a.Id));
        Assert.Equal((await port.FindApprovalAsync(110, Ct))?.Id, (await facade.FindApprovalAsync(110, Ct))?.Id);
        Assert.Equal(await port.GetPipelineIdForApprovalAsync(110, Ct), await facade.GetPipelineIdForApprovalAsync(110, Ct));
        Assert.Equal(
            (await port.GetPendingApprovalsAsync([1], Ct)).Select(a => a.ApprovalId),
            (await facade.GetPendingApprovalsAsync([1], Ct)).Select(a => a.ApprovalId));
        Assert.Equal(await port.GetExpiredPendingApprovalIdsAsync(Now, Ct), await facade.GetExpiredPendingApprovalIdsAsync(Now, Ct));
        Assert.Equal(await port.GetStrandedApprovalRunsAsync(Ct), await facade.GetStrandedApprovalRunsAsync(Ct));
    }

    public void Dispose() => _db.Dispose();
}
