// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-18: a run and a production environment show the expand/contract tracking of the database. The registry is
/// the only source of the pending contractions; the release from which a step is possible is derived from the first
/// successful deployment that applied the migration it waits for.
/// </summary>
public sealed class PipelineSchemaTrackingTests
{
    private static readonly DateTime At = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    private static PipelineMigrationGateVerdict Verdict(
        int runId, MigrationGateOutcome outcome, string migrationsJson, int id = 1) => new()
        {
            Id = id,
            PipelineRunId = runId,
            StageName = "Deploy",
            StepName = "Migrate",
            Outcome = outcome,
            PendingCount = 2,
            CreatedAt = At,
            MigrationsJson = migrationsJson
        };

    [Fact]
    public async Task Run_ClassifiesEachInspectedMigrationAsExpandOrContract()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        repo.ListForRunAsync(7, Arg.Any<CancellationToken>()).Returns(
        [
            Verdict(7, MigrationGateOutcome.Refused,
                "[{\"migrationId\":\"20261021_AddNote\",\"compatible\":true,\"violations\":[]},"
                + "{\"migrationId\":\"20261022_DropOld\",\"compatible\":false,\"violations\":[\"line 12\"]}]")
        ]);

        var schema = await new PipelineSchemaTrackingService(repo).GetForRunAsync(7, TestContext.Current.CancellationToken);

        var add = schema.Migrations.Single(m => m.MigrationId == "20261021_AddNote");
        Assert.Equal(SchemaMigrationKind.Expand, add.Kind);
        var drop = schema.Migrations.Single(m => m.MigrationId == "20261022_DropOld");
        Assert.Equal(SchemaMigrationKind.Contract, drop.Kind);
        Assert.Equal("line 12", Assert.Single(drop.Violations));
        // The step stopped on the refused gate: nothing was applied.
        Assert.All(schema.Migrations, m => Assert.False(m.Applied));
        Assert.Equal(7, schema.RunId);
        Assert.Single(schema.Gates);
    }

    [Fact]
    public async Task Run_AReviewedContractDropIsAContractEvenWhenTheGateLetItThrough()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        var reviewed = SchemaContractionRegistry.ContractMigrations[0];
        repo.ListForRunAsync(7, Arg.Any<CancellationToken>()).Returns(
        [
            Verdict(7, MigrationGateOutcome.Compatible,
                $"[{{\"migrationId\":\"{reviewed}\",\"compatible\":true,\"violations\":[]}}]")
        ]);

        var migration = Assert.Single(
            (await new PipelineSchemaTrackingService(repo).GetForRunAsync(7, TestContext.Current.CancellationToken)).Migrations);

        Assert.Equal(SchemaMigrationKind.Contract, migration.Kind);
        Assert.True(migration.Applied);
    }

    [Fact]
    public async Task Run_AMigrationInspectedTwiceKeepsItsLatestVerdict()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        repo.ListForRunAsync(7, Arg.Any<CancellationToken>()).Returns(
        [
            Verdict(7, MigrationGateOutcome.Refused, "[{\"migrationId\":\"M1\",\"compatible\":false,\"violations\":[\"x\"]}]", id: 1),
            Verdict(7, MigrationGateOutcome.Compatible, "[{\"migrationId\":\"M1\",\"compatible\":true,\"violations\":[]}]", id: 2)
        ]);

        var migration = Assert.Single(
            (await new PipelineSchemaTrackingService(repo).GetForRunAsync(7, TestContext.Current.CancellationToken)).Migrations);

        Assert.Equal(SchemaMigrationKind.Expand, migration.Kind);
        Assert.True(migration.Applied);
    }

    [Fact]
    public async Task Pending_ListsEveryRegistryStep_WithoutInventingAReleaseNobodyRecorded()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        repo.ListForRunAsync(7, Arg.Any<CancellationToken>()).Returns([Verdict(7, MigrationGateOutcome.NoPending, "[]")]);
        repo.FindFirstAppliedReleaseVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var schema = await new PipelineSchemaTrackingService(repo).GetForRunAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal(SchemaContractionRegistry.Pending.Select(step => step.Id), schema.PendingContractions.Select(c => c.Id));
        Assert.All(schema.PendingContractions, c =>
        {
            Assert.Null(c.AppliedInRelease);
            Assert.Null(c.PossibleFromRelease);
        });
    }

    // The run page hides the Schema tab of a run without a gate record, so the several queries that date each
    // pending contraction are not paid on every load of such a run.
    [Fact]
    public async Task Run_WithoutAGateRecord_ComputesNoPendingContraction()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        repo.ListForRunAsync(7, Arg.Any<CancellationToken>()).Returns([]);

        var schema = await new PipelineSchemaTrackingService(repo).GetForRunAsync(7, TestContext.Current.CancellationToken);

        Assert.Empty(schema.Gates);
        Assert.Empty(schema.Migrations);
        Assert.Empty(schema.PendingContractions);
        await repo.DidNotReceive().FindFirstAppliedReleaseVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pending_TheReleaseAfterTheOneThatAppliedTheRequiredMigrationIsTheEarliest()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        repo.ListForRunAsync(7, Arg.Any<CancellationToken>()).Returns([Verdict(7, MigrationGateOutcome.NoPending, "[]")]);
        repo.FindFirstAppliedReleaseVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var vaultDrop = SchemaContractionRegistry.Pending.Single(step => step.Id == "vault-secret-version-encrypted-value-drop");
        repo.FindFirstAppliedReleaseVersionAsync(vaultDrop.RequiredAfterMigration!, Arg.Any<CancellationToken>()).Returns("1.5.230");

        var schema = await new PipelineSchemaTrackingService(repo).GetForRunAsync(7, TestContext.Current.CancellationToken);

        var vault = schema.PendingContractions.Single(c => c.Id == vaultDrop.Id);
        Assert.Equal("1.5.230", vault.AppliedInRelease);
        Assert.Equal("1.5.231", vault.PossibleFromRelease);
        Assert.Equal(vaultDrop.Columns, vault.Columns);
        Assert.Equal(SchemaContractionStep.Drop, vault.Step);
        // A step that waits for another step has no release to compute, and none is looked up for it.
        var waiting = schema.PendingContractions.First(c => c.RequiredAfterMigration is null);
        Assert.Null(waiting.PossibleFromRelease);
    }

    [Theory]
    [InlineData("1.5.230", "1.5.231")]
    [InlineData("2.0.9", "2.0.10")]
    [InlineData("v1.5.0", "v1.5.1")]
    [InlineData("1.5.0-rc", null)]
    [InlineData("nightly", null)]
    public void NextRelease_IncrementsTheTrailingNumberOrSaysNothing(string version, string? expected) =>
        Assert.Equal(expected, PipelineSchemaTrackingService.NextRelease(version));

    [Fact]
    public async Task Environment_ReturnsThePendingContractionsAndTheLastGate()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        repo.EnvironmentExistsAsync(3, Arg.Any<CancellationToken>()).Returns(true);
        repo.FindLatestForEnvironmentAsync(3, Arg.Any<CancellationToken>())
            .Returns(Verdict(9, MigrationGateOutcome.NoPending, "[]"));

        var schema = await new PipelineSchemaTrackingService(repo).GetForEnvironmentAsync(3, TestContext.Current.CancellationToken);

        Assert.NotNull(schema);
        Assert.Equal(3, schema.EnvironmentId);
        Assert.Equal(9, schema.LastGate!.RunId);
        Assert.Equal(MigrationGateOutcome.NoPending, schema.LastGate.Outcome);
        Assert.Equal(SchemaContractionRegistry.Pending.Count, schema.PendingContractions.Count);
    }

    [Fact]
    public async Task Environment_WithoutAGateStillListsThePendingContractions()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();
        repo.EnvironmentExistsAsync(3, Arg.Any<CancellationToken>()).Returns(true);

        var schema = await new PipelineSchemaTrackingService(repo).GetForEnvironmentAsync(3, TestContext.Current.CancellationToken);

        Assert.Null(schema!.LastGate);
        Assert.NotEmpty(schema.PendingContractions);
    }

    [Fact]
    public async Task Environment_UnknownIsNull()
    {
        var repo = Substitute.For<IPipelineMigrationGateRepository>();

        Assert.Null(await new PipelineSchemaTrackingService(repo).GetForEnvironmentAsync(3, TestContext.Current.CancellationToken));
        await repo.DidNotReceive().FindLatestForEnvironmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunEndpoint_RefusesAReaderWhoCannotReadThePipeline()
    {
        var (sut, runs, authz, tracking) = RunController();
        runs.GetPipelineIdForRunAsync(5, Arg.Any<CancellationToken>()).Returns(1);
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await sut.GetRunSchemaTracking(5, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await tracking.DidNotReceive().GetForRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunEndpoint_AnswersNotFoundForAnUnknownRun()
    {
        var (sut, runs, _, _) = RunController();
        runs.GetPipelineIdForRunAsync(5, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await sut.GetRunSchemaTracking(5, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RunEndpoint_ReturnsTheTrackingToAReader()
    {
        var (sut, runs, authz, tracking) = RunController();
        runs.GetPipelineIdForRunAsync(5, Arg.Any<CancellationToken>()).Returns(1);
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        tracking.GetForRunAsync(5, Arg.Any<CancellationToken>()).Returns(new PipelineRunSchemaDto { RunId = 5 });

        var result = await sut.GetRunSchemaTracking(5, TestContext.Current.CancellationToken);

        Assert.Equal(5, Assert.IsType<PipelineRunSchemaDto>(Assert.IsType<OkObjectResult>(result.Result).Value).RunId);
    }

    [Fact]
    public async Task EnvironmentEndpoint_RefusesAReaderWhoCannotReadTheEnvironment()
    {
        var (sut, _, authz, tracking) = EnvironmentController();
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 3, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await sut.GetEnvironmentSchemaTracking(3, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await tracking.DidNotReceive().GetForEnvironmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnvironmentEndpoint_AnswersNotFoundForAnUnknownEnvironment()
    {
        var (sut, _, authz, tracking) = EnvironmentController();
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 3, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        tracking.GetForEnvironmentAsync(3, Arg.Any<CancellationToken>()).Returns((EnvironmentSchemaDto?)null);

        var result = await sut.GetEnvironmentSchemaTracking(3, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task EnvironmentEndpoint_ReturnsTheTrackingToAReader()
    {
        var (sut, _, authz, tracking) = EnvironmentController();
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 3, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        tracking.GetForEnvironmentAsync(3, Arg.Any<CancellationToken>()).Returns(new EnvironmentSchemaDto { EnvironmentId = 3 });

        var result = await sut.GetEnvironmentSchemaTracking(3, TestContext.Current.CancellationToken);

        Assert.Equal(3, Assert.IsType<EnvironmentSchemaDto>(Assert.IsType<OkObjectResult>(result.Result).Value).EnvironmentId);
    }

    // The last verdict belongs to a pipeline run: reading the environment is not enough to read it.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task EnvironmentEndpoint_ShowsTheLastGateOnlyToAReaderOfItsPipeline(bool canReadPipeline, bool gateShown)
    {
        var (sut, runs, authz, tracking) = EnvironmentController();
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 3, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        runs.GetPipelineIdForRunAsync(9, Arg.Any<CancellationToken>()).Returns(4);
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 4, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(canReadPipeline);
        tracking.GetForEnvironmentAsync(3, Arg.Any<CancellationToken>()).Returns(new EnvironmentSchemaDto
        {
            EnvironmentId = 3,
            PendingContractions = [new PendingContractionDto { Id = "step" }],
            LastGate = new PipelineRunMigrationGateDto { RunId = 9, Outcome = MigrationGateOutcome.Compatible }
        });

        var result = await sut.GetEnvironmentSchemaTracking(3, TestContext.Current.CancellationToken);

        var schema = Assert.IsType<EnvironmentSchemaDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(gateShown, schema.LastGate is not null);
        // The pending contractions come from the registry, not from a run: the environment reader keeps them.
        Assert.Equal("step", Assert.Single(schema.PendingContractions).Id);
    }

    [Fact]
    public async Task EnvironmentEndpoint_OmitsTheLastGateWhenItsRunIsGone()
    {
        var (sut, runs, authz, tracking) = EnvironmentController();
        authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 3, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
        runs.GetPipelineIdForRunAsync(9, Arg.Any<CancellationToken>()).Returns((int?)null);
        tracking.GetForEnvironmentAsync(3, Arg.Any<CancellationToken>()).Returns(new EnvironmentSchemaDto
        {
            EnvironmentId = 3,
            LastGate = new PipelineRunMigrationGateDto { RunId = 9 }
        });

        var result = await sut.GetEnvironmentSchemaTracking(3, TestContext.Current.CancellationToken);

        Assert.Null(Assert.IsType<EnvironmentSchemaDto>(Assert.IsType<OkObjectResult>(result.Result).Value).LastGate);
    }

    private static ControllerContext Caller() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
        }
    };

    private static (PipelineRunSchemaTrackingController Sut, IPipelineRunService Runs, IResourceAuthorizationService Authz, IPipelineSchemaTrackingService Tracking) RunController()
    {
        var runs = Substitute.For<IPipelineRunService>();
        var authz = Substitute.For<IResourceAuthorizationService>();
        var tracking = Substitute.For<IPipelineSchemaTrackingService>();
        return (new PipelineRunSchemaTrackingController(runs, tracking, authz) { ControllerContext = Caller() }, runs, authz, tracking);
    }

    private static (EnvironmentSchemaTrackingController Sut, IPipelineRunService Runs, IResourceAuthorizationService Authz, IPipelineSchemaTrackingService Tracking) EnvironmentController()
    {
        var runs = Substitute.For<IPipelineRunService>();
        var authz = Substitute.For<IResourceAuthorizationService>();
        var tracking = Substitute.For<IPipelineSchemaTrackingService>();
        return (new EnvironmentSchemaTrackingController(runs, tracking, authz) { ControllerContext = Caller() }, runs, authz, tracking);
    }
}
