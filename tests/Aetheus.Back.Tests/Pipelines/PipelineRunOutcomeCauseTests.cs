// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Recette R1-9: the result mail of a root run names why a Failed or Rejected run ended so.</summary>
public sealed class PipelineRunOutcomeCauseTests : IDisposable
{
    private readonly AppDbContext _db;

    public PipelineRunOutcomeCauseTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private static StepFailureProjection Failed(string? reason, string? code = null, int? exit = null, string? leg = null) =>
        new("Deploy", "restart", leg, exit, code, reason);

    [Fact]
    public void AFailure_PrefersTheRecordedReason_ThenTheCode_ThenTheExitCode()
    {
        Assert.Equal("Disk full (E_DISK)", PipelineRunOutcomeCause.ForFailure([Failed("Disk full", "E_DISK", 1)]).Cause);
        Assert.Equal("Disk full", PipelineRunOutcomeCause.ForFailure([Failed("Disk full", null, 1)]).Cause);
        Assert.Equal("E_DISK", PipelineRunOutcomeCause.ForFailure([Failed(null, "E_DISK", 1)]).Cause);
        Assert.Equal("exit code 137", PipelineRunOutcomeCause.ForFailure([Failed(null, null, 137)]).Cause);
    }

    [Fact]
    public void AFailureWithNothingRecorded_InventsNoCause_ButStillNamesTheStep()
    {
        var cause = PipelineRunOutcomeCause.ForFailure([Failed(null, null, null, "linux")]);

        Assert.Null(cause.Cause);
        Assert.Equal("Deploy / restart (linux)", cause.CauseStep);
        Assert.Equal(PipelineRunOutcomeCause.None, PipelineRunOutcomeCause.ForFailure([]));
    }

    [Fact]
    public void ALongCause_IsClipped_AndAtMostThreeStepsAreNamed()
    {
        var steps = Enumerable.Range(1, 5).Select(index => new StepFailureProjection("S", $"step{index}", null, 1, null, new string('x', 900))).ToList();

        var cause = PipelineRunOutcomeCause.ForFailure(steps);

        Assert.Equal(PipelineRunOutcomeCause.MaxCauseLength, cause.Cause!.Length);
        Assert.Equal("S / step1, S / step2, S / step3", cause.CauseStep);
    }

    [Fact]
    public void ARejection_NamesTheRefusingOutputs_NotTheAdvisoryOnes()
    {
        var cause = PipelineRunOutcomeCause.ForRejection([
            new StepOutputProjection("Qa", "unit", """{"UNIT_TEST_GATE_STATUS":"2","SMOKE_GATE_STATUS":"1"}"""),
            new StepOutputProjection("Qa", "clean", """{"UNIT_TEST_GATE_STATUS":"0"}"""),
            new StepOutputProjection("Qa", "seal", """{"CANDIDATE_DEPLOYABLE":"false"}""")]);

        Assert.Equal("UNIT_TEST_GATE_STATUS=2, CANDIDATE_DEPLOYABLE=false", cause.Cause);
        Assert.Equal("Qa / unit, Qa / seal", cause.CauseStep);
        Assert.Equal(PipelineRunOutcomeCause.None, PipelineRunOutcomeCause.ForRejection(
            [new StepOutputProjection("Qa", "clean", """{"CANDIDATE_DEPLOYABLE":"true"}""")]));
    }

    [Fact]
    public async Task GetFailedStepsAsync_ReadsTheTasksOwnReasonFirst_BlockingFailuresBeforeSwallowedOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.Tasks.Add(new ServerTask { Id = 70, FailureCode = "TASK_CODE", FailureReason = "task said so" });
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun { PipelineRunId = 5, Order = 1, Status = TaskExecutionStatus.Failed, ContinueOnError = true, StageName = "A", StepName = "soft", FailureReason = "swallowed" },
            new PipelineStepRun { PipelineRunId = 5, Order = 2, Status = TaskExecutionStatus.Failed, StageName = "B", StepName = "hard", FailureReason = "step reason", TaskId = 70, MatrixLeg = "arm" },
            new PipelineStepRun { PipelineRunId = 5, Order = 3, Status = TaskExecutionStatus.Success, StageName = "C", StepName = "fine", FailureReason = "not a failure" },
            new PipelineStepRun { PipelineRunId = 6, Order = 1, Status = TaskExecutionStatus.Failed, StageName = "Z", StepName = "other run" });
        await _db.SaveChangesAsync(ct);
        var repo = new PipelineRepository(_db, TimeProvider.System, NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System), new PipelineRunLineageRepository(_db));

        var failed = await repo.GetFailedStepsAsync(5, 3, ct);

        Assert.Equal(["hard", "soft"], failed.Select(step => step.StepName));
        Assert.Equal("task said so", failed[0].FailureReason);
        Assert.Equal("TASK_CODE", failed[0].FailureCode);
        Assert.Equal("arm", failed[0].MatrixLeg);
        Assert.Equal("swallowed", failed[1].FailureReason);
        Assert.Single(await repo.GetFailedStepsAsync(5, 1, ct));
    }

    [Theory]
    [InlineData("en", "Cause", "Step concerned")]
    [InlineData("fr", "Cause", "Étape concernée")]
    public void TheResultMail_ListsTheCauseAndTheStep_InTheRecipientsLanguage(string language, string causeLabel, string stepLabel)
    {
        var email = new NotificationEmail("reader@example.org", "pipeline.failed in project Aetheus", NotificationEventTypes.PipelineFailed,
            """{"PipelineRunId":2698,"PipelineId":41,"PipelineName":"deploy","BuildNumber":11,"ProjectId":1,"Status":"Failed","Cause":"Disk full (E_DISK)","CauseStep":"Deploy / restart","IsChildRun":false,"RootRunId":2698,"RootPipelineName":null}""");

        var mail = NotificationMailKit.Build(email, "https://aetheus.example.org", languageJson: $"\"{language}\"");

        Assert.Contains($"{causeLabel}\tDisk full (E_DISK)", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains($"{stepLabel}\tDeploy / restart", mail.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AResultWithoutCause_ListsNoCauseLine()
    {
        var email = new NotificationEmail("reader@example.org", "pipeline.succeeded in project Aetheus", NotificationEventTypes.PipelineSucceeded,
            """{"PipelineRunId":1,"PipelineId":41,"PipelineName":"deploy","BuildNumber":11,"ProjectId":1,"Status":"Success","Cause":null,"CauseStep":null,"IsChildRun":false,"RootRunId":1,"RootPipelineName":null}""");

        var mail = NotificationMailKit.Build(email, "https://aetheus.example.org");

        Assert.DoesNotContain("Cause\t", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Step concerned", mail.TextBody, StringComparison.Ordinal);
    }
}
