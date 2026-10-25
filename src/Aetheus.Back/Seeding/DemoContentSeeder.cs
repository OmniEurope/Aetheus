// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Seeding;

/// <summary>
/// Materializes the demo data that cannot be represented by database rows alone: the Toto bare Git
/// repository and downloadable artifact files. Every operation is idempotent so it is safe to run
/// after the dated relational seed marker has already been written.
/// </summary>
public sealed class DemoContentSeeder(
    AppDbContext db,
    IGitLightService git,
    IPipelineGitService pipelineGit,
    IArtifactStorageService artifactStorage,
    TimeProvider timeProvider,
    ILogger<DemoContentSeeder> logger)
{
    private const string SeedActor = "demo-seed";

    public async Task SeedAsync(DemoSeedResult seed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seed);

        await SeedTotoRepositoryAsync(seed.TotoProjectId, ct).ConfigureAwait(false);
        await SeedArtifactsAsync(seed, ct).ConfigureAwait(false);
    }

    private async Task SeedTotoRepositoryAsync(int projectId, CancellationToken ct)
    {
        var repositories = await git.GetRepositoriesAsync(projectId, ct).ConfigureAwait(false);
        var repository = repositories.SingleOrDefault(candidate =>
            string.Equals(candidate.Slug, "toto", StringComparison.OrdinalIgnoreCase));
        if (repository is null)
        {
            repository = await git.CreateRepositoryAsync(new CreateGitLightRepoRequest
            {
                ProjectId = projectId,
                Name = "toto",
                Description = "Dépôt de démonstration versionné pour les tests local et QA",
                DefaultBranch = "main"
            }, ct).ConfigureAwait(false);
        }

        await git.EnsureRepositoryInitializedAsync(repository.Id, ct).ConfigureAwait(false);
        foreach (var (pipelineName, yaml) in DemoDataSeeder.TotoPipelineDefinitions())
        {
            var existing = await pipelineGit.ReadProjectPipelineYamlAsync(
                projectId, pipelineName, ct, repository.DefaultBranch, repository.Id).ConfigureAwait(false);
            if (string.Equals(existing, yaml, StringComparison.Ordinal))
                continue;

            var (outcome, error) = await pipelineGit.WriteProjectPipelineYamlAsync(
                projectId, pipelineName, yaml, SeedActor, ct, repository.DefaultBranch, repository.Id).ConfigureAwait(false);
            if (outcome != GitWriteOutcome.Committed)
                throw new InvalidOperationException($"Could not seed Toto pipeline '{pipelineName}' in Git: {error ?? outcome.ToString()}.");
        }

        var trackedRepository = await db.GitInternalRepos
            .SingleAsync(candidate => candidate.Id == repository.Id, ct).ConfigureAwait(false);
        trackedRepository.IsEmpty = false;
        trackedRepository.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        trackedRepository.LastPushAt ??= trackedRepository.UpdatedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var branches = await git.GetBranchesAsync(repository.Id, ct).ConfigureAwait(false);
        foreach (var name in new[] { "qa", "release/demo" })
        {
            if (branches.Any(branch => string.Equals(branch.Name, name, StringComparison.Ordinal)))
                continue;

            await git.CreateBranchAsync(repository.Id, new CreateGitLightBranchRequest
            {
                Name = name,
                StartRef = repository.DefaultBranch
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task SeedArtifactsAsync(DemoSeedResult seed, CancellationToken ct)
    {
        var versionDate = DemoDataSeeder.ResolveVersionDate(seed.SeedDate);
        var anchor = DateTime.SpecifyKind(versionDate.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc);
        var candidates = await db.PipelineRuns
            .Where(run => run.Pipeline.ProjectId == seed.TotoProjectId && run.Status == PipelineStatus.Success)
            .OrderByDescending(run => run.StartedAt)
            .Select(run => new
            {
                Run = run,
                Pipeline = run.Pipeline,
                ProjectId = run.Pipeline.ProjectId!.Value
            })
            .Take(2)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            var fileName = $"{candidate.Pipeline.Name}-{candidate.Run.Id}-result.txt";
            var artifact = await db.PipelineArtifacts.SingleOrDefaultAsync(
                item => item.PipelineRunId == candidate.Run.Id && item.Name == fileName, ct).ConfigureAwait(false);
            if (artifact is not null)
            {
                using var existingFile = artifactStorage.OpenArtifact(artifact.FilePath);
                if (existingFile is not null)
                    continue;
            }

            var bytes = Encoding.UTF8.GetBytes(
                $"Aetheus demo artifact\nseed={seed.SeedDate}\npipeline={candidate.Pipeline.Name}\nrun={candidate.Run.Id}\n");
            await using var content = new MemoryStream(bytes, writable: false);
            var (relativePath, sha256) = await artifactStorage.SaveArtifactAsync(
                candidate.ProjectId, candidate.Pipeline.Id, candidate.Run.Id, fileName, content, ct).ConfigureAwait(false);

            artifact ??= new PipelineArtifact
            {
                PipelineRunId = candidate.Run.Id,
                PipelineId = candidate.Pipeline.Id,
                ProjectId = candidate.ProjectId,
                Name = fileName,
                StageName = "verify",
                StepName = "check",
                CreatedAt = candidate.Run.CompletedAt ?? candidate.Run.StartedAt,
                RetentionPolicy = ArtifactRetentionPolicy.Build,
                RetentionExpiresAt = anchor.AddYears(10)
            };
            artifact.FilePath = relativePath;
            artifact.SizeBytes = bytes.LongLength;
            artifact.Sha256 = sha256;
            if (artifact.Id == 0)
                db.PipelineArtifacts.Add(artifact);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Materialized demo Git repository and {ArtifactCount} downloadable artifacts.", candidates.Count);
    }
}
