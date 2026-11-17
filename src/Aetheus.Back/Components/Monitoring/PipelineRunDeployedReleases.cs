// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Monitoring;

/// <summary>
/// R-34: which release a deployment run delivers. A deployment run checks out the head of its source branch (that
/// is the commit the run lists), but what it puts in production is the candidate release it restores, named by the
/// <c>AETHEUS_CANDIDATE_VERSION</c> variable the run carries. Redeploying an older candidate therefore leaves
/// production on the candidate's commit while the run still lists the branch head: this reads the recorded release
/// back so the run, its list rows and its approval name the commit and version that are really deployed.
/// Read from the run's resolved variables and the project's release record, never guessed.
/// </summary>
public static class PipelineRunDeployedReleases
{
    /// <summary>The run variable naming the release a deployment run delivers.</summary>
    public const string CandidateVersionVariable = "AETHEUS_CANDIDATE_VERSION";

    /// <summary>The release the run names, from its resolved variables JSON; null when it names none or the name is
    /// a placeholder that never resolved.</summary>
    public static string? ReadCandidateVersion(string? resolvedVariablesJson)
    {
        if (string.IsNullOrWhiteSpace(resolvedVariablesJson)) return null;
        try
        {
            var variables = JsonSerializer.Deserialize<Dictionary<string, string>>(resolvedVariablesJson);
            if (variables is null || !variables.TryGetValue(CandidateVersionVariable, out var value)) return null;
            var version = value?.Trim();
            return string.IsNullOrEmpty(version) || version.Contains("$(", StringComparison.Ordinal) ? null : version;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The recorded release set beside the source head the run checked out.</summary>
    /// <param name="includeAlreadyDeployed">Whether to read <see cref="ReleaseStatus.Deployed"/> as "already in
    /// production". Only true for a run still waiting for its approval: a run that has deployed marks its own
    /// release deployed, which says nothing about what it replaced.</param>
    public static PipelineRunDeployedReleaseDto Describe(
        int releaseId, string version, ReleaseStatus status, string? releaseCommit,
        string? sourceBranch, string? sourceCommit, bool includeAlreadyDeployed, int? repositoryId = null) => new()
        {
            RepositoryId = repositoryId,
            ReleaseId = releaseId,
            Version = version,
            CommitHash = releaseCommit,
            Status = status,
            SourceBranch = sourceBranch,
            SourceCommitHash = sourceCommit,
            IsBehindSourceHead = !SameCommit(releaseCommit, sourceCommit),
            IsAlreadyDeployed = includeAlreadyDeployed && status == ReleaseStatus.Deployed
        };

    /// <summary>False only when both commits are known and differ. A commit missing on either side proves nothing, so
    /// it is never a warning; a short hash matches the full one it starts.</summary>
    private static bool SameCommit(string? release, string? source)
    {
        if (string.IsNullOrWhiteSpace(release) || string.IsNullOrWhiteSpace(source)) return true;
        var (shorter, longer) = release.Length <= source.Length ? (release, source) : (source, release);
        return longer.StartsWith(shorter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The deployed release of each of the given runs that names one the project has recorded. A run that
    /// names none, or a release the project does not have, is absent from the result.</summary>
    public static async Task<Dictionary<int, PipelineRunDeployedReleaseDto>> ResolveAsync(
        AppDbContext db, IReadOnlyCollection<int> runIds, bool includeAlreadyDeployed, CancellationToken ct)
    {
        var result = new Dictionary<int, PipelineRunDeployedReleaseDto>();
        if (runIds.Count == 0) return result;

        // Only the runs that carry the variable are read, so a page of ordinary runs loads no variables at all.
        var candidates = await db.PipelineRuns.AsNoTracking()
            .Where(run => runIds.Contains(run.Id)
                && run.Pipeline != null && run.Pipeline.ProjectId != null
                && run.ResolvedVariablesJson != null && run.ResolvedVariablesJson.Contains(CandidateVersionVariable))
            .Select(run => new
            {
                run.Id,
                ProjectId = run.Pipeline!.ProjectId!.Value,
                run.ResolvedVariablesJson,
                run.BranchName,
                run.CommitHash,
                run.RepositoryUrl
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var named = candidates
            .Select(run => (Run: run, Version: ReadCandidateVersion(run.ResolvedVariablesJson)))
            .Where(item => item.Version is not null)
            .ToList();
        if (named.Count == 0) return result;

        var projectIds = named.Select(item => item.Run.ProjectId).Distinct().ToList();
        var versions = named.Select(item => item.Version!).Distinct().ToList();
        var releases = await db.Releases.AsNoTracking()
            .Where(release => projectIds.Contains(release.ProjectId) && versions.Contains(release.Version))
            .OrderByDescending(release => release.DetectedAt)
            .Select(release => new { release.Id, release.ProjectId, release.Version, release.Status, release.CommitHash })
            .ToListAsync(ct).ConfigureAwait(false);
        // Newest first, like the advance-branch step: the first release of a (project, version) pair wins.
        var byKey = releases
            .GroupBy(release => (release.ProjectId, release.Version))
            .ToDictionary(group => group.Key, group => group.First());

        var repositoryIds = await PipelineRunRepositoryLinks
            .ResolveAsync(db, named.Select(item => item.Run.RepositoryUrl), ct).ConfigureAwait(false);
        foreach (var (run, version) in named)
        {
            if (!byKey.TryGetValue((run.ProjectId, version!), out var release)) continue;
            result[run.Id] = Describe(
                release.Id, release.Version, release.Status, release.CommitHash,
                run.BranchName, run.CommitHash, includeAlreadyDeployed,
                PipelineRunRepositoryLinks.Find(repositoryIds, run.RepositoryUrl));
        }
        return result;
    }

    /// <summary>Completes <see cref="PipelineRunDto.DeployedRelease"/> on list-projected runs.</summary>
    public static async Task<List<PipelineRunDto>> ApplyAsync(
        AppDbContext db, List<PipelineRunDto> runs, CancellationToken ct)
    {
        var resolved = await ResolveAsync(db, runs.Select(run => run.Id).ToList(), includeAlreadyDeployed: false, ct)
            .ConfigureAwait(false);
        if (resolved.Count == 0) return runs;
        return runs.Select(run => resolved.TryGetValue(run.Id, out var release) ? run with { DeployedRelease = release } : run)
            .ToList();
    }
}
