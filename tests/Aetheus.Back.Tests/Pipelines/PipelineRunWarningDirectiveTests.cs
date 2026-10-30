// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-75: a QA that deployed no environment succeeds on purpose, so the step says so with a
/// <c>RUN_WARNING</c> output variable. The variable is read at task completion, written on the run, and written on
/// the run that triggered it (the candidate), without turning anything into a failure.
/// </summary>
public sealed class PipelineRunWarningDirectiveTests : IDisposable
{
    private const string QaWarning = "No QA environment was deployed: nothing was validated against a running application.";

    private readonly AppDbContext _db;
    private readonly PipelineRepository _repo;

    public PipelineRunWarningDirectiveTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PipelineRepository(
            _db,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(_db, TimeProvider.System),
            new PipelineRunLineageRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Parse_ReadsTheWarningTheStepPublishedAsAnOutputVariable()
    {
        var output = TaskOutputVariableParser.Parse(
            $"Preparing the stack\r\n##aetheus[setvariable name=QA_STACK_DEPLOYED]false\r\n##aetheus[setvariable name=RUN_WARNING]{QaWarning}\r\nDone");

        Assert.Equal([QaWarning], TaskRunWarningParser.Parse(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("WARNING: COMPOSE_FILE is empty in this pipeline")]
    [InlineData("##aetheus[setvariable name=RUN_WARNING]   ")]
    [InlineData("##aetheus[setvariable name=QA_STACK_DEPLOYED]false")]
    public void Parse_IgnoresWhatIsNotAWarning(string logOutput) =>
        Assert.Empty(TaskRunWarningParser.Parse(TaskOutputVariableParser.Parse(logOutput)));

    [Fact]
    public void Parse_BoundsWhatAScriptCouldWrite()
    {
        var output = TaskOutputVariableParser.Parse("##aetheus[setvariable name=RUN_WARNING]" + new string('x', 5000));

        var warning = Assert.Single(TaskRunWarningParser.Parse(output));

        Assert.Equal(TaskRunWarningParser.MaxWarningLength, warning.Length);
    }

    /// <summary>Recette R-23 (W8) and R-113: a step names its warning with <c>RUN_WARNING_CODE</c>, so the warning is stored
    /// as a problem the front says in the user's language; the English text stays inside it for the API and the logs.</summary>
    [Fact]
    public void Parse_AWarningWithItsCode_IsStoredAsAProblem()
    {
        var output = TaskOutputVariableParser.Parse(
            $"##aetheus[setvariable name=RUN_WARNING]{QaWarning}\n##aetheus[setvariable name=RUN_WARNING_CODE]QaE2eCommandMissing");

        var warning = Assert.Single(TaskRunWarningParser.Parse(output));

        Assert.Equal("RunWarning_QaE2eCommandMissing", StoredProblem.CodeOf(warning));
        Assert.Equal(QaWarning, StoredProblem.English(warning));
    }

    /// <summary>A code a script could write anything into is only a name: one that is not a plain identifier leaves the
    /// warning as the free text it was.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Qa E2e")]
    [InlineData("../Other")]
    [InlineData("Qa{run}")]
    public void Parse_ACodeThatIsNotAnIdentifier_KeepsTheFreeText(string code)
    {
        var output = TaskOutputVariableParser.Parse(
            $"##aetheus[setvariable name=RUN_WARNING]{QaWarning}\n##aetheus[setvariable name=RUN_WARNING_CODE]{code}");

        Assert.Equal([QaWarning], TaskRunWarningParser.Parse(output));
    }

    /// <summary>The candidate says the QA's coded warning inside its own, so both are said in the user's language.</summary>
    [Fact]
    public async Task Handler_WrapsACodedWarningOfTheChild_SoTheCandidateSaysItInTheUsersLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (candidateRun, qaRun, _) = await SeedCandidateWithQaAsync(ct);
        var coded = Assert.Single(TaskRunWarningParser.Parse(TaskOutputVariableParser.Parse(
            $"##aetheus[setvariable name=RUN_WARNING]{QaWarning}\n##aetheus[setvariable name=RUN_WARNING_CODE]QaE2eCommandMissing")));

        await new PipelineStepRunWarningsHandler(_repo)
            .HandleAsync(new PipelineStepRunWarningsReportedEvent(qaRun.Id, [coded]), ct);

        _db.ChangeTracker.Clear();
        Assert.Equal([coded], await WarningsOfAsync(qaRun.Id, ct));
        var propagated = Assert.Single(await WarningsOfAsync(candidateRun.Id, ct));
        Assert.True(StoredProblem.TryRead(propagated, out var content));
        Assert.Equal(PipelineProblemCodes.ChildRunWarning, content.Problem.Code);
        Assert.Equal("RunWarning_QaE2eCommandMissing", Assert.Single(content.Inner).Code);
        Assert.Contains(QaWarning, content.English, StringComparison.Ordinal);
        Assert.DoesNotContain("problem:", content.English, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskCompletion_DispatchesTheWarningOfASucceededStepWithItsRun()
    {
        var repo = Substitute.For<ITaskRepository>();
        var events = Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>();
        var stepRun = new PipelineStepRun { Id = 11, PipelineRunId = 7, StageName = "Prepare QA stack", StepName = "Write the disposable stack environment" };
        repo.FindPipelineStepRunAsync(11, Arg.Any<CancellationToken>()).Returns(stepRun);
        var finalizer = new TaskCompletionFinalizer(
            repo,
            Substitute.For<Aetheus.Back.Components.Logs.ILogService>(),
            Substitute.For<Aetheus.Back.Components.Audit.IAuditService>(),
            TimeProvider.System,
            Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskService>.Instance,
            events);

        var updated = await finalizer.UpdateStepRunAsync(
            new ServerTask { Id = 21, PipelineRunId = 7, PipelineStepRunId = 11 },
            new TaskResultDto
            {
                TaskId = 21,
                Status = TaskExecutionStatus.Success,
                ExitCode = 0,
                Output = $"##aetheus[setvariable name=QA_STACK_DEPLOYED]false\n##aetheus[setvariable name=RUN_WARNING]{QaWarning}"
            },
            21,
            TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Success, updated!.Status);
        await events.Received(1).DispatchStrictAsync(
            Arg.Is<PipelineStepRunWarningsReportedEvent>(e => e.PipelineRunId == 7 && e.Warnings.SequenceEqual(new[] { QaWarning })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_PutsTheWarningOnTheQaRunAndOnTheCandidateThatTriggeredIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (candidateRun, qaRun, otherRun) = await SeedCandidateWithQaAsync(ct);

        await new PipelineStepRunWarningsHandler(_repo)
            .HandleAsync(new PipelineStepRunWarningsReportedEvent(qaRun.Id, [QaWarning]), ct);

        _db.ChangeTracker.Clear();
        var qaWarnings = await WarningsOfAsync(qaRun.Id, ct);
        var candidateWarnings = await WarningsOfAsync(candidateRun.Id, ct);
        Assert.Equal([QaWarning], qaWarnings);
        var propagated = Assert.Single(candidateWarnings);
        Assert.Contains(QaWarning, propagated, StringComparison.Ordinal);
        Assert.Contains("'QA'", propagated, StringComparison.Ordinal);
        Assert.Contains($"#{qaRun.Id}", propagated, StringComparison.Ordinal);
        Assert.Empty(await WarningsOfAsync(otherRun.Id, ct));
        Assert.Equal(PipelineStatus.Success, (await _db.PipelineRuns.FindAsync([qaRun.Id], ct))!.Status);
        Assert.Equal(PipelineStatus.Running, (await _db.PipelineRuns.FindAsync([candidateRun.Id], ct))!.Status);
    }

    private async Task<(PipelineRun Candidate, PipelineRun Qa, PipelineRun Other)> SeedCandidateWithQaAsync(CancellationToken ct)
    {
        var pipeline = new Pipeline { Name = "P", YamlDefinition = "y" };
        _db.Pipelines.Add(pipeline);
        await _db.SaveChangesAsync(ct);
        var candidate = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        var qa = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = DateTime.UtcNow };
        var other = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Running, StartedAt = DateTime.UtcNow };
        _db.PipelineRuns.AddRange(candidate, qa, other);
        await _db.SaveChangesAsync(ct);
        _db.PipelineStepRuns.AddRange(
            new PipelineStepRun
            {
                PipelineRunId = candidate.Id,
                StageName = "QA",
                StepName = "Validate V",
                Order = 1,
                Status = TaskExecutionStatus.Running,
                TriggeredRunId = qa.Id
            },
            new PipelineStepRun
            {
                PipelineRunId = other.Id,
                StageName = "Elsewhere",
                StepName = "Other",
                Order = 1,
                Status = TaskExecutionStatus.Running,
                TriggeredRunId = null
            });
        await _db.SaveChangesAsync(ct);
        return (candidate, qa, other);
    }

    private async Task<List<string>> WarningsOfAsync(int runId, CancellationToken ct)
    {
        var run = await _db.PipelineRuns.AsNoTracking().SingleAsync(r => r.Id == runId, ct);
        return string.IsNullOrEmpty(run.WarningsJson) ? [] : JsonSerializer.Deserialize<List<string>>(run.WarningsJson)!;
    }
}
