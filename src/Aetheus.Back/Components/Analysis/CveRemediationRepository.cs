// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Git;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aetheus.Back.Components.Analysis;

/// <summary>Who a match belongs to: found first, before anything else of it is read, so a refused caller learns nothing.</summary>
public sealed record CveRemediationScope(long MatchId, int OrganizationId, int ProjectId);

/// <summary>What a remediation needs of its match.</summary>
internal sealed record CveRemediationFacts(
    VulnerabilityMatchStatus Status, bool Closed, string Advisory, string Ecosystem, string PackageName, string Version, string? SourcePath, int? RepositoryId,
    ProjectInventoryItemKind? ItemKind = null);

/// <summary>A repository as a remediation sees it: <paramref name="IsMirror"/> when its connection points at an external provider.</summary>
internal sealed record CveRemediationRepositoryRow(GitInternalRepo Repository, bool IsMirror);

internal sealed record CveRemediationPullRequest(int Id, int Number, PullRequestStatus Status);

/// <summary>The rows behind the remediations of the watch.</summary>
internal sealed class CveRemediationRepository(AppDbContext db)
{
    private const string UniqueViolation = "23505";

    private static readonly VulnerabilityRemediationStatus[] Active =
    [
        VulnerabilityRemediationStatus.Requested, VulnerabilityRemediationStatus.Running,
        VulnerabilityRemediationStatus.BranchPushed, VulnerabilityRemediationStatus.PullRequestOpen
    ];

    /// <summary>The match, when the caller may read it: <paramref name="organizationIds"/> null for a site Admin, otherwise the organizations
    /// of the caller, and <paramref name="readableProjectIds"/> the read scope. Outside both it is not found, as if it did not exist.</summary>
    public async Task<CveRemediationScope?> FindScopeAsync(
        long matchId, IReadOnlyCollection<int>? organizationIds, IReadOnlyCollection<int>? readableProjectIds, CancellationToken ct)
    {
        var query = db.VulnerabilityMatches.AsNoTracking().Where(match => match.Id == matchId);
        if (organizationIds is not null)
        {
            var organizations = organizationIds.ToArray();
            query = query.Where(match => organizations.Contains(match.OrganizationId));
        }

        if (readableProjectIds is not null)
        {
            var readable = readableProjectIds.ToArray();
            query = query.Where(match => readable.Contains(match.ProjectId));
        }

        return await query.Select(match => new CveRemediationScope(match.Id, match.OrganizationId, match.ProjectId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public Task<CveRemediationFacts?> GetFactsAsync(long matchId, CancellationToken ct) =>
        db.VulnerabilityMatches.AsNoTracking()
            .Where(match => match.Id == matchId)
            .Select(match => new CveRemediationFacts(
                match.Status, match.ClosedAt != null, match.Advisory.PrimaryCveId ?? match.Advisory.ExternalId, match.Ecosystem, match.PackageName, match.Version,
                match.ProjectInventoryItem == null ? null : match.ProjectInventoryItem.SourcePath,
                match.ProjectInventoryItem == null ? null : match.ProjectInventoryItem.ProjectInventory.GitInternalRepoId,
                match.ProjectInventoryItem == null ? null : (ProjectInventoryItemKind?)match.ProjectInventoryItem.Kind))
            .FirstOrDefaultAsync(ct);

    public async Task<CveRemediationRepositoryRow?> FindRepositoryAsync(int repositoryId, int projectId, CancellationToken ct)
    {
        var row = await db.GitInternalRepos.AsNoTracking()
            .Where(repository => repository.Id == repositoryId && repository.ProjectId == projectId && !repository.IsConfiguration && !repository.IsEmpty)
            .Select(repository => new
            {
                Repository = repository,
                Provider = repository.GitConnection == null ? (GitProviderType?)null : repository.GitConnection.ProviderType
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : new CveRemediationRepositoryRow(row.Repository, row.Provider is not (null or GitProviderType.AetheusGit));
    }

    public Task<VulnerabilityRemediation?> GetLatestAsync(long matchId, CancellationToken ct) =>
        db.VulnerabilityRemediations.Where(row => row.VulnerabilityMatchId == matchId)
            .OrderByDescending(row => row.Id).FirstOrDefaultAsync(ct);

    public Task<VulnerabilityRemediation?> GetActiveAsync(long matchId, CancellationToken ct) =>
        db.VulnerabilityRemediations.Where(row => row.VulnerabilityMatchId == matchId && Active.Contains(row.Status))
            .FirstOrDefaultAsync(ct);

    /// <summary>Whether another match is preparing this branch: two matches of one advisory and package share its name.</summary>
    public Task<bool> IsBranchTakenAsync(int repositoryId, string branch, long matchId, CancellationToken ct) =>
        db.VulnerabilityRemediations.AsNoTracking().AnyAsync(row =>
            row.GitInternalRepoId == repositoryId && row.Branch == branch && row.VulnerabilityMatchId != matchId && Active.Contains(row.Status), ct);

    /// <summary>Adds the attempt. False when the unique index found another active one first: asking twice never opens a second branch.</summary>
    public async Task<bool> TryAddAsync(VulnerabilityRemediation remediation, CancellationToken ct)
    {
        db.VulnerabilityRemediations.Add(remediation);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.Entry(remediation).State = EntityState.Detached;
            return false;
        }
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <summary>Takes the end of the run: the event and a read can both come, and only the one this update answers goes on to the push.
    /// The branch stands as pushed by then, the status says the run is over. False when another one took it first.</summary>
    public async Task<bool> TryClaimRunEndAsync(VulnerabilityRemediation remediation, DateTime now)
    {
        var rows = db.VulnerabilityRemediations.Where(row => row.Id == remediation.Id && row.Status == VulnerabilityRemediationStatus.Running);
        var claimed = 0;
        if (db.Database.IsRelational())
        {
            claimed = await rows.ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.Status, VulnerabilityRemediationStatus.BranchPushed)
                    .SetProperty(row => row.UpdatedAt, now), CancellationToken.None)
                .ConfigureAwait(false);
            await db.Entry(remediation).ReloadAsync(CancellationToken.None).ConfigureAwait(false);
            return claimed == 1;
        }

        // The in-memory provider has no bulk update: same change, row by row.
        foreach (var row in await rows.ToListAsync(CancellationToken.None).ConfigureAwait(false))
        {
            row.Status = VulnerabilityRemediationStatus.BranchPushed;
            row.UpdatedAt = now;
            claimed++;
        }

        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        return claimed == 1;
    }

    /// <summary>Ends an attempt still active. It goes past the change tracker on purpose: what failed may be a change pending in this very
    /// context, which a save would only fail on again, and it does not depend on the request that may have gone.</summary>
    public async Task WriteFailureAsync(VulnerabilityRemediation remediation, string reason, DateTime now)
    {
        var resolved = remediation.ResolvedVersion;
        var rows = db.VulnerabilityRemediations.Where(row => row.Id == remediation.Id && Active.Contains(row.Status));
        if (db.Database.IsRelational())
        {
            await rows.ExecuteUpdateAsync(update => update
                    .SetProperty(row => row.Status, VulnerabilityRemediationStatus.Failed)
                    .SetProperty(row => row.FailureReason, reason)
                    .SetProperty(row => row.ResolvedVersion, resolved)
                    .SetProperty(row => row.UpdatedAt, now)
                    .SetProperty(row => row.EndedAt, now), CancellationToken.None)
                .ConfigureAwait(false);
            await db.Entry(remediation).ReloadAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // The in-memory provider has no bulk update: same change, row by row.
        foreach (var row in await rows.ToListAsync(CancellationToken.None).ConfigureAwait(false))
        {
            row.Status = VulnerabilityRemediationStatus.Failed;
            row.FailureReason = reason;
            row.UpdatedAt = now;
            row.EndedAt = now;
        }

        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The status of a run, or null when the run no longer exists.</summary>
    public Task<PipelineStatus?> GetRunStatusAsync(int runId, CancellationToken ct) =>
        db.PipelineRuns.AsNoTracking()
            .Where(run => run.Id == runId)
            .Select(run => (PipelineStatus?)run.Status)
            .FirstOrDefaultAsync(ct);

    public Task<int?> FindPipelineAsync(int projectId, string name, CancellationToken ct) =>
        db.Pipelines.AsNoTracking()
            .Where(pipeline => pipeline.ProjectId == projectId && pipeline.Name == name)
            .Select(pipeline => (int?)pipeline.Id)
            .FirstOrDefaultAsync(ct);

    public Task<List<VulnerabilityRemediation>> GetRunningOfRunAsync(int runId, CancellationToken ct) =>
        db.VulnerabilityRemediations
            .Where(row => row.PipelineRunId == runId && row.Status == VulnerabilityRemediationStatus.Running)
            .ToListAsync(ct);

    public Task<List<VulnerabilityRemediation>> GetOpenOfRepositoryAsync(int repositoryId, CancellationToken ct) =>
        db.VulnerabilityRemediations
            .Where(row => row.GitInternalRepoId == repositoryId && row.Status == VulnerabilityRemediationStatus.PullRequestOpen)
            .ToListAsync(ct);

    public Task<CveRemediationPullRequest?> GetPullRequestAsync(int pullRequestId, CancellationToken ct) =>
        db.PullRequests.AsNoTracking()
            .Where(request => request.Id == pullRequestId)
            .Select(request => new CveRemediationPullRequest(request.Id, request.ExternalId, request.Status))
            .FirstOrDefaultAsync(ct);

    /// <summary>The pull request already open from this branch: a retry reuses it instead of opening a second one.</summary>
    public Task<CveRemediationPullRequest?> FindOpenPullRequestAsync(int connectionId, string source, string target, CancellationToken ct) =>
        db.PullRequests.AsNoTracking()
            .Where(request => request.GitConnectionId == connectionId && request.SourceBranch == source && request.TargetBranch == target
                              && request.Status == PullRequestStatus.Open)
            .Select(request => new CveRemediationPullRequest(request.Id, request.ExternalId, request.Status))
            .FirstOrDefaultAsync(ct);
}
