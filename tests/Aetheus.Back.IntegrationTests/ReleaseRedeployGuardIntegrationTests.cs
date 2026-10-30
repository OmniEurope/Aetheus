// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// R-72, on PostgreSQL: deploying an older version is not refused by the back. The two things a deployment launch
/// asks of the release it names, the Gate's lookup (grade) and the retained artifact its restore step needs, do not
/// look at the release's status, so a superseded release is as deployable as the live one while its artifact is
/// kept; a release whose artifact was freed by retention is refused before the run starts, with the code the launch
/// shows. The front offers "Redéployer cette version" under exactly that rule.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReleaseRedeployGuardIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    private const string PayloadName = "ApplicationPayload-artifacts";
    private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seed(int ProjectId);

    private async Task<Seed> SeedProjectAsync()
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var organization = new Organization { Name = "Redeploy", Slug = $"redeploy-{Guid.NewGuid():N}" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(Ct);
        var project = new Project { Name = $"redeploy-{Guid.NewGuid():N}", OrganizationId = organization.Id };
        var pipeline = new Pipeline { Name = $"redeploy-{Guid.NewGuid():N}" };
        db.Projects.Add(project);
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync(Ct);
        var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        // c-oldest: superseded long ago, artifact freed by retention. c-previous: superseded, artifact kept.
        // c-live: deployed, artifact kept.
        AddRelease(db, project, pipeline, run, "c-oldest", ReleaseStatus.Superseded, AnalysisGrade.B, 1, withArtifact: false);
        AddRelease(db, project, pipeline, run, "c-previous", ReleaseStatus.Superseded, AnalysisGrade.B, 2, withArtifact: true);
        AddRelease(db, project, pipeline, run, "c-live", ReleaseStatus.Deployed, AnalysisGrade.A, 3, withArtifact: true);
        await db.SaveChangesAsync(Ct);
        return new Seed(project.Id);
    }

    private static void AddRelease(
        AppDbContext db, Project project, Pipeline pipeline, PipelineRun run, string version, ReleaseStatus status,
        AnalysisGrade grade, int day, bool withArtifact)
    {
        var when = new DateTime(2026, 9, day, 12, 0, 0, DateTimeKind.Utc);
        var release = new Release
        {
            ProjectId = project.Id,
            Version = version,
            Status = status,
            DetectedAt = when,
            PublishedAt = when,
            AssuranceGrade = grade,
            Deployable = true
        };
        if (withArtifact)
        {
            release.Artifacts =
            [
                new PipelineArtifact
                {
                    PipelineId = pipeline.Id,
                    ProjectId = project.Id,
                    PipelineRunId = run.Id,
                    Name = PayloadName,
                    FilePath = $"/artifacts/{version}",
                    Sha256 = ValidSha256,
                    CreatedAt = when,
                    RetentionExpiresAt = when.AddDays(30)
                }
            ];
        }
        db.Releases.Add(release);
    }

    private static PipelineYamlDefinition DeploymentRestore() => new()
    {
        Name = "demo-deploy-prod",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Restore candidate", Os = "linux",
                Steps =
                [
                    new PipelineStepDefinition
                    {
                        Name = "Restore immutable candidate payload", Type = "restore-artifacts",
                        Release = "$(APPLICATION_CANDIDATE_VERSION)", Artifact = PayloadName
                    }
                ]
            }
        ]
    };

    private static IArtifactStorageService StorageHoldingEveryPayload()
    {
        var storage = Substitute.For<IArtifactStorageService>();
        storage.OpenArtifact(Arg.Any<string>()).Returns(_ => new MemoryStream([1]));
        return storage;
    }

    private static Dictionary<string, string> Version(string version) =>
        new(StringComparer.OrdinalIgnoreCase) { ["APPLICATION_CANDIDATE_VERSION"] = version };

    [Fact]
    public async Task AnOlderSupersededRelease_WhoseArtifactIsKept_PassesTheDeploymentGateLookupAndTheRestoreCheck()
    {
        var seed = await SeedProjectAsync();
        await using var db = NewContext();

        var gateRelease = await new PipelineServerResolver(db).FindDeploymentGateReleaseAsync(seed.ProjectId, "c-previous", Ct);
        var problems = await new PipelineReleaseArtifactPreflight(new ArtifactRepository(db), StorageHoldingEveryPayload())
            .FindProblemsAsync(DeploymentRestore(), Version("c-previous"), seed.ProjectId, Ct);

        Assert.NotNull(gateRelease);
        Assert.Equal("c-previous", gateRelease.Version);
        Assert.Equal(AnalysisGrade.B, gateRelease.AssuranceGrade);
        Assert.Equal(true, gateRelease.Deployable);
        Assert.Empty(problems);
    }

    [Fact]
    public async Task TheLiveRelease_IsNotTreatedDifferentlyFromAnOlderOne()
    {
        var seed = await SeedProjectAsync();
        await using var db = NewContext();

        var problems = await new PipelineReleaseArtifactPreflight(new ArtifactRepository(db), StorageHoldingEveryPayload())
            .FindProblemsAsync(DeploymentRestore(), Version("c-live"), seed.ProjectId, Ct);

        Assert.Empty(problems);
    }

    [Fact]
    public async Task AReleaseWhoseArtifactWasFreed_IsRefusedBeforeTheRun_WithTheNotRetainedCode()
    {
        var seed = await SeedProjectAsync();
        await using var db = NewContext();

        var problems = await new PipelineReleaseArtifactPreflight(new ArtifactRepository(db), StorageHoldingEveryPayload())
            .FindProblemsAsync(DeploymentRestore(), Version("c-oldest"), seed.ProjectId, Ct);

        var problem = Assert.Single(problems);
        Assert.Equal(PipelineProblemCodes.ReleaseArtifactNotRetained, problem.Code);
        Assert.Contains("c-oldest", problem.English, StringComparison.Ordinal);
        Assert.Contains(PayloadName, problem.English, StringComparison.Ordinal);
        // The Gate's lookup still knows the release: only its missing payload stops the deployment.
        Assert.NotNull(await new PipelineServerResolver(db).FindDeploymentGateReleaseAsync(seed.ProjectId, "c-oldest", Ct));
    }

    [Fact]
    public async Task AReleaseOfAnotherProject_IsNeverFoundByVersion()
    {
        var seed = await SeedProjectAsync();
        await using var db = NewContext();

        var problems = await new PipelineReleaseArtifactPreflight(new ArtifactRepository(db), StorageHoldingEveryPayload())
            .FindProblemsAsync(DeploymentRestore(), Version("c-previous"), seed.ProjectId + 1000, Ct);

        Assert.Equal(PipelineProblemCodes.ReleaseArtifactNotRetained, Assert.Single(problems).Code);
    }
}
