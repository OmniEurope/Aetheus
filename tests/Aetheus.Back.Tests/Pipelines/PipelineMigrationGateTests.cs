// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Components.Tasks.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R1-43: the expand/contract verdict a blue-green migrate step writes as a
/// <c>##aetheus[migration-gate]</c> line is parsed at task completion, kept against the run, and
/// read back for the run page by those who may read the pipeline.
/// </summary>
public sealed class PipelineMigrationGateTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTime At = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

    private static string Line(MigrationGateReport report) =>
        MigrationGateReport.Directive + JsonSerializer.Serialize(report, Web);

    [Fact]
    public void Parse_ReadsTheReportAmongOrdinaryLogLines()
    {
        var output = string.Join('\n',
            "Pending migrations are not expand-compatible. Ship expand and contract in separate releases:",
            "20261014_Drop: line 12 is not expand-compatible: migrationBuilder.DropColumn(",
            Line(new MigrationGateReport
            {
                Outcome = MigrationGateOutcome.Refused,
                PendingCount = 2,
                Migrations =
                [
                    new MigrationGateMigrationVerdict { MigrationId = "20261013_Add", Compatible = true },
                    new MigrationGateMigrationVerdict
                    {
                        MigrationId = "20261014_Drop",
                        Violations = ["20261014_Drop: line 12 is not expand-compatible: migrationBuilder.DropColumn("]
                    }
                ]
            }) + "\r");

        var verdict = Assert.Single(TaskMigrationGateParser.Parse(output, 7, "Deploy", "Migrate", At));

        Assert.Equal(7, verdict.PipelineRunId);
        Assert.Equal("Deploy", verdict.StageName);
        Assert.Equal("Migrate", verdict.StepName);
        Assert.Equal(MigrationGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(2, verdict.PendingCount);
        Assert.Equal(At, verdict.CreatedAt);
        var migrations = JsonSerializer.Deserialize<List<MigrationGateMigrationVerdict>>(verdict.MigrationsJson, Web)!;
        Assert.Equal(["20261013_Add", "20261014_Drop"], migrations.Select(m => m.MigrationId));
        Assert.Single(migrations[1].Violations);
    }

    [Theory]
    [InlineData("##aetheus[migration-gate]not json")]
    [InlineData("##aetheus[migration-gate]{\"outcome\":\"Sideways\"}")]
    [InlineData("##aetheus[migration-gate]{\"outcome\":42}")]
    [InlineData("No pending EF migration; the schema is already current.")]
    public void Parse_SkipsWhatIsNotAWellFormedReport(string output) =>
        Assert.Empty(TaskMigrationGateParser.Parse(output, 7, null, null, At));

    [Fact]
    public void Parse_BoundsWhatAForgedLineCouldCarry()
    {
        var longText = new string('x', 5000);
        var output = Line(new MigrationGateReport
        {
            Outcome = MigrationGateOutcome.Unavailable,
            Detail = longText,
            Migrations = [.. Enumerable.Range(0, 300).Select(i => new MigrationGateMigrationVerdict
            {
                MigrationId = $"M{i}",
                Violations = [.. Enumerable.Range(0, 50).Select(_ => longText)]
            })]
        });

        var verdict = Assert.Single(TaskMigrationGateParser.Parse(output, 7, null, null, At));

        Assert.Equal(MigrationGateReport.MaxTextLength, verdict.Detail!.Length);
        var migrations = JsonSerializer.Deserialize<List<MigrationGateMigrationVerdict>>(verdict.MigrationsJson, Web)!;
        Assert.Equal(MigrationGateReport.MaxMigrations, migrations.Count);
        Assert.All(migrations, m =>
        {
            Assert.Equal(MigrationGateReport.MaxViolationsPerMigration, m.Violations.Count);
            Assert.All(m.Violations, v => Assert.Equal(MigrationGateReport.MaxTextLength, v.Length));
        });
    }

    [Fact]
    public async Task TaskCompletion_DispatchesTheVerdictOfTheMigrateStepWithItsRunAndStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = Substitute.For<ITaskRepository>();
        var events = Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>();
        var stepRun = new PipelineStepRun { Id = 11, PipelineRunId = 7, StageName = "Deploy", StepName = "Migrate" };
        repo.FindPipelineStepRunAsync(11, Arg.Any<CancellationToken>()).Returns(stepRun);
        var finalizer = new TaskCompletionFinalizer(
            repo,
            Substitute.For<Aetheus.Back.Components.Logs.ILogService>(),
            Substitute.For<Aetheus.Back.Components.Audit.IAuditService>(),
            TimeProvider.System,
            Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskService>.Instance,
            events);
        var task = new ServerTask { Id = 21, PipelineRunId = 7, PipelineStepRunId = 11 };

        await finalizer.UpdateStepRunAsync(
            task,
            new TaskResultDto
            {
                TaskId = 21,
                Status = TaskExecutionStatus.Failed,
                ExitCode = 1,
                Output = Line(new MigrationGateReport { Outcome = MigrationGateOutcome.Refused, PendingCount = 1 })
            },
            21,
            ct);

        await events.Received(1).DispatchStrictAsync(
            Arg.Is<PipelineStepMigrationGateReportedEvent>(e =>
                e.PipelineRunId == 7
                && e.Verdicts.Count == 1
                && e.Verdicts[0].Outcome == MigrationGateOutcome.Refused
                && e.Verdicts[0].StepName == "Migrate"
                && e.Verdicts[0].StageName == "Deploy"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_StoresTheVerdicts()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        var verdicts = new List<PipelineMigrationGateVerdict> { new() { PipelineRunId = 7, Outcome = MigrationGateOutcome.Compatible } };

        await new PipelineStepMigrationGateHandler(repo).HandleAsync(
            new PipelineStepMigrationGateReportedEvent(7, verdicts), TestContext.Current.CancellationToken);

        await repo.Received(1).AddAsync(verdicts, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Service_MapsEachRowWithItsMigrations()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        repo.ListForRunAsync(7, Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineMigrationGateVerdict
            {
                Id = 3, PipelineRunId = 7, StageName = "Deploy", StepName = "Migrate",
                Outcome = MigrationGateOutcome.Compatible, PendingCount = 1, CreatedAt = At,
                MigrationsJson = "[{\"migrationId\":\"20261013_Add\",\"compatible\":true,\"violations\":[]}]"
            }
        ]);

        var dto = Assert.Single(await new PipelineMigrationGateService(repo).GetForRunAsync(7, TestContext.Current.CancellationToken));

        Assert.Equal(3, dto.Id);
        Assert.Equal(MigrationGateOutcome.Compatible, dto.Outcome);
        Assert.Equal(At, dto.RecordedAt);
        Assert.Equal("20261013_Add", Assert.Single(dto.Migrations).MigrationId);
    }

    [Fact]
    public async Task Controller_RefusesAReaderWhoCannotReadThePipeline()
    {
        var (sut, runs, authz, gates) = Controller();
        runs.GetPipelineIdForRunAsync(5, Arg.Any<CancellationToken>()).Returns(1);
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await sut.GetRunMigrationGate(5, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await gates.DidNotReceive().GetForRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Controller_AnswersNotFoundForAnUnknownRun()
    {
        var (sut, runs, _, _) = Controller();
        runs.GetPipelineIdForRunAsync(5, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await sut.GetRunMigrationGate(5, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Controller_ReturnsTheRunsVerdicts()
    {
        var (sut, runs, authz, gates) = Controller();
        runs.GetPipelineIdForRunAsync(5, Arg.Any<CancellationToken>()).Returns(1);
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        gates.GetForRunAsync(5, Arg.Any<CancellationToken>())
            .Returns([new PipelineRunMigrationGateDto { Id = 1, Outcome = MigrationGateOutcome.NoPending }]);

        var result = await sut.GetRunMigrationGate(5, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(MigrationGateOutcome.NoPending, Assert.Single(Assert.IsType<List<PipelineRunMigrationGateDto>>(ok.Value)).Outcome);
    }

    private static (PipelineRunMigrationGateController Sut, IPipelineRunService Runs, IResourceAuthorizationService Authz, IPipelineMigrationGateService Gates) Controller()
    {
        var runs = Substitute.For<IPipelineRunService>();
        var authz = Substitute.For<IResourceAuthorizationService>();
        var gates = Substitute.For<IPipelineMigrationGateService>();
        var sut = new PipelineRunMigrationGateController(runs, gates, authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
                }
            }
        };
        return (sut, runs, authz, gates);
    }
}
