// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Components.VulnerabilityWatch;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Inventory;

public interface IProjectInventoryService
{
    /// <summary>Reads every source of one project and stores what each says, gaps included.</summary>
    Task RefreshProjectAsync(int projectId, CancellationToken ct);
}

/// <summary>One read at a time in this process, whatever asked for it: a burst of pushes stays light on a small server.</summary>
internal sealed class ProjectInventoryGate
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

/// <summary>
/// Refreshes the inventories of a project: the default branch of each repository, the commit of the
/// last release when it differs, the last SBOM. Every project ends with at least one row saying what is
/// known, so a project with nothing to read shows as not covered, never as free of vulnerabilities.
/// </summary>
internal sealed class ProjectInventoryService(
    IProjectInventoryRepository repository,
    RepositoryInventoryReader reader,
    ProjectInventoryGate gate,
    IVulnerabilityMatchService matching,
    IOptions<VulnerabilityWatchOptions> options,
    TimeProvider timeProvider,
    ILogger<ProjectInventoryService> logger) : IProjectInventoryService
{
    private readonly VulnerabilityWatchOptions _options = options.Value;

    public async Task RefreshProjectAsync(int projectId, CancellationToken ct)
    {
        await gate.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var target = await repository.GetTargetAsync(projectId, ct).ConfigureAwait(false);
            if (target is null) return;
            var known = await repository.GetKnownAsync(projectId, ct).ConfigureAwait(false);
            var written = new HashSet<(ProjectInventoryOrigin, int?)>();

            var heads = await ReadRepositoriesAsync(target, known, written, ct).ConfigureAwait(false);
            await ReadReleaseAsync(target, heads, known, written, ct).ConfigureAwait(false);
            await ReadSbomAsync(target, known, written, ct).ConfigureAwait(false);
            await ReadServersAsync(target, written, ct).ConfigureAwait(false);

            var stale = known.Where(item => !written.Contains((item.Origin, item.RepositoryId))).Select(item => item.Id).ToList();
            await repository.RemoveAsync(projectId, stale, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Lock.Release();
        }

        await MatchAsync(projectId, ct).ConfigureAwait(false);
    }

    /// <summary>The read is stored whatever happens here: what was not matched stays pending for the feed cycle.</summary>
    private async Task MatchAsync(int projectId, CancellationToken ct)
    {
        try
        {
            await matching.MatchChangedInventoriesAsync(projectId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Vulnerability matching of project {ProjectId} failed", projectId);
        }
    }

    private async Task<List<string>> ReadRepositoriesAsync(
        ProjectInventoryTarget target, IReadOnlyList<KnownInventory> known, HashSet<(ProjectInventoryOrigin, int?)> written, CancellationToken ct)
    {
        var heads = new List<string>();
        if (target.Repositories.Count == 0)
        {
            var code = string.IsNullOrWhiteSpace(target.RepositoryUrl) ? InventoryGapCodes.NoRepository : InventoryGapCodes.AddressOnly;
            await StoreAsync(target, null, ProjectInventoryOrigin.Repository, null, null, null, InventoryBuilder.Empty(new InventoryGap(code)), written, ct).ConfigureAwait(false);
            return heads;
        }

        foreach (var source in target.Repositories)
        {
            var read = await ReadAsync(known, ProjectInventoryOrigin.Repository, source.Id, $"refs/heads/{source.DefaultBranch}", ct).ConfigureAwait(false);
            if (read is not null) heads.Add(read.CommitSha);
            var result = read is null
                ? InventoryBuilder.Empty(new InventoryGap(source.IsEmpty ? InventoryGapCodes.RepositoryEmpty : InventoryGapCodes.RepositoryUnreadable))
                : read.Result;
            await StoreAsync(target, source.Id, ProjectInventoryOrigin.Repository, source.DefaultBranch, read?.CommitSha, read?.Fingerprint, result, written, ct).ConfigureAwait(false);
        }

        return heads;
    }

    /// <summary>What runs in production may be older than the default branch, and only then is it read apart.</summary>
    private async Task ReadReleaseAsync(
        ProjectInventoryTarget target, List<string> heads, IReadOnlyList<KnownInventory> known,
        HashSet<(ProjectInventoryOrigin, int?)> written, CancellationToken ct)
    {
        var commit = target.ReleaseCommit;
        if (string.IsNullOrEmpty(commit) || heads.Any(head => head.StartsWith(commit, StringComparison.OrdinalIgnoreCase))) return;
        foreach (var source in commit.All(char.IsAsciiHexDigit) ? target.Repositories : [])
        {
            var read = await ReadAsync(known, ProjectInventoryOrigin.ReleaseCommit, source.Id, commit, ct).ConfigureAwait(false);
            if (read is null) continue;
            await StoreAsync(target, source.Id, ProjectInventoryOrigin.ReleaseCommit, commit, read.CommitSha, read.Fingerprint, read.Result, written, ct).ConfigureAwait(false);
            return;
        }

        await StoreAsync(
            target, null, ProjectInventoryOrigin.ReleaseCommit, commit, commit, null,
            InventoryBuilder.Empty(new InventoryGap(InventoryGapCodes.ReleaseCommitNotFound)), written, ct).ConfigureAwait(false);
    }

    private Task<RepositoryInventory?> ReadAsync(
        IReadOnlyList<KnownInventory> known, ProjectInventoryOrigin origin, int repositoryId, string revision, CancellationToken ct)
    {
        var fingerprint = known.FirstOrDefault(item => item.Origin == origin && item.RepositoryId == repositoryId)?.Fingerprint;
        return reader.ReadAsync(repositoryId, revision, fingerprint, ct);
    }

    private async Task ReadSbomAsync(
        ProjectInventoryTarget target, IReadOnlyList<KnownInventory> known, HashSet<(ProjectInventoryOrigin, int?)> written, CancellationToken ct)
    {
        var sbom = await repository.GetLatestSbomAsync(target.ProjectId, target.OrganizationId, _options.MaxInventoryItems, ct).ConfigureAwait(false);
        if (sbom is null)
        {
            await StoreAsync(target, null, ProjectInventoryOrigin.Sbom, null, null, null, InventoryBuilder.Empty(new InventoryGap(InventoryGapCodes.NoSbom)), written, ct).ConfigureAwait(false);
            return;
        }

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', sbom.ReportIds))));
        var unchanged = known.Any(item => item.Origin == ProjectInventoryOrigin.Sbom && item.Fingerprint == fingerprint);
        InventoryBuildResult? result = null;
        if (!unchanged)
        {
            var (entries, gaps) = SbomInventoryMapper.Map(sbom, _options.MaxInventoryItems);
            result = InventoryBuilder.Build(entries, gaps, new HashSet<string>(), _options.MaxInventoryItems);
        }

        await StoreAsync(target, null, ProjectInventoryOrigin.Sbom, sbom.BranchName, sbom.CommitHash, fingerprint, result, written, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The images the servers of the project list, outside any pipeline. A project with no server link has nothing to say
    /// at this origin and gets no row (a previous one is removed): an empty row would claim a server that is not there.
    /// </summary>
    private async Task ReadServersAsync(ProjectInventoryTarget target, HashSet<(ProjectInventoryOrigin, int?)> written, CancellationToken ct)
    {
        var snapshot = await repository.GetServerSnapshotAsync(target.ProjectId, ct).ConfigureAwait(false);
        if (!snapshot.HasLinks) return;
        var (entries, gaps) = ServerInventoryMapper.Map(snapshot);
        var result = InventoryBuilder.Build(entries, gaps, new HashSet<string>(), _options.MaxInventoryItems);
        await StoreAsync(target, null, ProjectInventoryOrigin.Server, null, null, null, result, written, ct).ConfigureAwait(false);
    }

    private Task StoreAsync(
        ProjectInventoryTarget target, int? repositoryId, ProjectInventoryOrigin origin, string? reference, string? commit,
        string? fingerprint, InventoryBuildResult? result, HashSet<(ProjectInventoryOrigin, int?)> written, CancellationToken ct)
    {
        written.Add((origin, repositoryId));
        return repository.SaveAsync(
            new ProjectInventoryDraft(
                target.ProjectId, target.OrganizationId, repositoryId, origin, reference, commit, fingerprint, result,
                timeProvider.GetUtcNow().UtcDateTime),
            ct);
    }
}
